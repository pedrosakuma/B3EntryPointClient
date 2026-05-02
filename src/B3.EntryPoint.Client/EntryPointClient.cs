using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Logging;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.Risk;
using B3.EntryPoint.Client.State;
using B3.EntryPoint.Client.Telemetry;
using Microsoft.Extensions.Logging;
using ClOrdID = B3.EntryPoint.Client.Models.ClOrdID;

namespace B3.EntryPoint.Client;

/// <summary>
/// High-level B3 EntryPoint client. <see cref="ConnectAsync"/> performs TCP
/// connect + Negotiate + Establish; <see cref="DisposeAsync"/> sends Terminate.
/// Order submission, replace, cancel and event streaming are exposed as the
/// public API surface; their wire-level implementations land incrementally
/// (see <c>docs/CONFORMANCE.md</c> and the issues tagged <c>area/api-surface</c>).
/// </summary>
public sealed class EntryPointClient : IEntryPointClient, ISubmitOrder, IReplaceOrder, ICancelOrder, ISubmitCross, IQuoteFlow
{
    private readonly EntryPointClientOptions _options;
    private readonly Channel<EntryPointEvent> _events;
    private TcpClient? _tcp;
    private FixpClientSession? _session;
    private KeepAliveScheduler? _keepAlive;
    private RetransmitRequestHandler? _retransmit;
    private DateTime _lastInboundUtc;
    private CancellationTokenSource? _idleCts;
    private Task? _idleWatchdog;
    private Channel<PersistOp>? _persistChannel;
    private CancellationTokenSource? _persistCts;
    private Task? _persistWorker;

    private readonly ConcurrentDictionary<string, ulong> _outstandingOrders = new(StringComparer.Ordinal);
    private long _deltasSinceCompact;
    private ulong _lastInboundSeqNum;

    /// <summary>
    /// Persistence operation enqueued from the inbound loop and drained by a
    /// single dedicated worker per session lifetime. Replaces the legacy
    /// fire-and-forget <c>Task.Run</c> in
    /// <see cref="OnInboundEventForPersistence"/> so persistence work is
    /// tracked and deterministically drained on teardown (#121).
    /// </summary>
    private readonly record struct PersistOp(string ClOrdID, ulong InboundSeqNum);

    /// <summary>Keep-alive scheduler for this client. Bound after <see cref="ConnectAsync"/>.</summary>
    public IKeepAliveScheduler? KeepAlive => _keepAlive;

    /// <summary>Retransmit handler for this client. Bound after <see cref="ConnectAsync"/>.</summary>
    public IRetransmitRequestHandler? Retransmit => _retransmit;

    /// <summary>
    /// Pre-trade risk gates. Evaluated in registration order before any order
    /// entry frame leaves the client. The first non-Allow decision throws
    /// <see cref="RiskRejectedException"/>.
    /// </summary>
    public IList<IPreTradeGate> RiskGates { get; } = new List<IPreTradeGate>();

    public EntryPointClient(EntryPointClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _options = options;
        _events = Channel.CreateBounded<EntryPointEvent>(new BoundedChannelOptions(options.EventChannelCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    private static void ValidateOptions(EntryPointClientOptions options)
    {
        if (options.Endpoint is null)
            throw new ArgumentException($"{nameof(EntryPointClientOptions.Endpoint)} is required.", nameof(options));
        if (options.Credentials is null)
            throw new ArgumentException($"{nameof(EntryPointClientOptions.Credentials)} is required.", nameof(options));
        if (options.SessionId == 0u)
            throw new ArgumentException($"{nameof(EntryPointClientOptions.SessionId)} must be non-zero.", nameof(options));
        if (options.EnteringFirm == 0u)
            throw new ArgumentException($"{nameof(EntryPointClientOptions.EnteringFirm)} must be non-zero.", nameof(options));
        if (options.Tls is { Enabled: false, ClientCertificates: { Count: > 0 } })
            throw new ArgumentException($"{nameof(EntryPointClientOptions.Tls)}.{nameof(TlsOptions.ClientCertificates)} requires {nameof(TlsOptions)}.{nameof(TlsOptions.Enabled)} = true.", nameof(options));
    }

    public FixpClientState State => _session?.State ?? FixpClientState.Disconnected;

    /// <summary>
    /// Raised after a <c>Terminate</c> is sent or received. The session is no
    /// longer usable when this fires; a new <see cref="ConnectAsync"/> (with
    /// a bumped <see cref="EntryPointClientOptions.SessionVerId"/>) is
    /// required to resume.
    /// </summary>
    public event EventHandler<TerminatedEventArgs>? Terminated;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_session is not null)
            throw new InvalidOperationException("Client is already connected.");

        var attempts = Math.Max(1, _options.ConnectMaxAttempts);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ConnectOnceAsync(ct).ConfigureAwait(false);
                _options.Logger.Connected(attempt, attempts, _options.Endpoint);
                StartIdleWatchdog();
                return;
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                lastError = ex;
                var delay = ComputeBackoff(attempt);
                _options.Logger.ConnectRetry(ex, attempt, attempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                _options.Logger.ConnectExhausted(ex, attempts, _options.Endpoint);
                throw;
            }
        }
        if (lastError is not null)
            _options.Logger.ConnectExhausted(lastError, attempts, _options.Endpoint);
        throw lastError ?? new InvalidOperationException("ConnectAsync failed without exception.");
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        using var activity = EntryPointTelemetry.ActivitySource.StartActivity("entrypoint.connect", ActivityKind.Client);
        activity?.SetTag("net.peer.name", _options.Endpoint.Address.ToString());
        activity?.SetTag("net.peer.port", _options.Endpoint.Port);
        activity?.SetTag("net.transport", _options.Tls.Enabled ? "tls" : "tcp");
        var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(_options.Endpoint.Address, _options.Endpoint.Port, connectCts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        var transportStream = await EstablishTransportStreamAsync(tcp, ct).ConfigureAwait(false);
        _session = new FixpClientSession(transportStream, _options);

        using (var negotiate = EntryPointTelemetry.ActivitySource.StartActivity("entrypoint.negotiate", ActivityKind.Client))
        {
            await _session.NegotiateAsync(ct).ConfigureAwait(false);
            _options.Logger.Negotiated(_options.Endpoint);
        }
        using (var establish = EntryPointTelemetry.ActivitySource.StartActivity("entrypoint.establish", ActivityKind.Client))
        {
            await _session.EstablishAsync(ct).ConfigureAwait(false);
            _options.Logger.Established(_options.Endpoint);
        }

        await HydrateFromSnapshotAsync(ct).ConfigureAwait(false);

        StartPersistenceWorker();

        _session.OnInboundEvent = OnInboundEventForPersistence;

        _session.StartInboundLoop(_events.Writer);

        _keepAlive = new KeepAliveScheduler(
            _options.KeepAliveInterval,
            sendSequence: (seq, token) => _session.SendSequenceAsync(seq, token),
            nextSeqNo: () => _session.PeekNextOutboundSeqNum());
        _session.OnInboundSequence = nextSeq =>
        {
            _lastInboundUtc = DateTime.UtcNow;
            _keepAlive!.RaiseFrameReceived(nextSeq, DateTimeOffset.UtcNow);
        };
        _keepAlive.Start();

        _retransmit = new RetransmitRequestHandler(
            sendRequest: (from, count, token) => _session.SendRetransmitRequestAsync(from, count, token));
        _session.OnInboundRetransmission = (nextSeq, count, reqNanos) =>
        {
            _lastInboundUtc = DateTime.UtcNow;
            _retransmit!.RaiseRetransmissionReceived(nextSeq, count, NanosToOffset(reqNanos));
        };
        _session.OnInboundRetransmitReject = (code, reqNanos) =>
        {
            _lastInboundUtc = DateTime.UtcNow;
            _retransmit!.RaiseRetransmitRejected((B3.EntryPoint.Client.Fixp.RetransmitRejectCode)(byte)code, NanosToOffset(reqNanos));
        };
        _session.OnInboundNotApplied = (from, count) =>
        {
            _lastInboundUtc = DateTime.UtcNow;
            _retransmit!.RaiseNotAppliedReceived(from, count);
        };
        _session.OnInboundTerminate = code =>
        {
            _lastInboundUtc = DateTime.UtcNow;
            _options.Logger.InboundTerminate(code);
            EntryPointTelemetry.Terminations.Add(1,
                new KeyValuePair<string, object?>("direction", "inbound"),
                new KeyValuePair<string, object?>("code", code.ToString()));
            RaiseTerminated((TerminationCode)code, reason: null, initiatedByClient: false);
        };
        _lastInboundUtc = DateTime.UtcNow;
    }

    private async Task<System.IO.Stream> EstablishTransportStreamAsync(TcpClient tcp, CancellationToken ct)
    {
        var raw = tcp.GetStream();
        if (!_options.Tls.Enabled)
            return raw;

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false, _options.Tls.RemoteCertificateValidationCallback);
        try
        {
            var targetHost = _options.Tls.TargetHost ?? _options.Endpoint.Address.ToString();
            var auth = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                EnabledSslProtocols = _options.Tls.EnabledSslProtocols,
                ClientCertificates = _options.Tls.ClientCertificates,
            };
            await ssl.AuthenticateAsClientAsync(auth, ct).ConfigureAwait(false);
            _options.Logger.TlsHandshakeCompleted(_options.Endpoint, targetHost, ssl.SslProtocol.ToString());
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private TimeSpan ComputeBackoff(int attempt)
    {
        var baseMs = _options.ConnectBaseDelay.TotalMilliseconds;
        var raw = baseMs * Math.Pow(2, attempt - 1);
        var capped = Math.Min(raw, _options.ConnectMaxDelay.TotalMilliseconds);
        var jitter = Random.Shared.NextDouble() * 0.25 * capped;
        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    private void StartIdleWatchdog()
    {
        if (_options.IdleTimeout <= TimeSpan.Zero) return;
        _idleCts = new CancellationTokenSource();
        var token = _idleCts.Token;
        _idleWatchdog = Task.Run(async () =>
        {
            var period = TimeSpan.FromMilliseconds(Math.Max(50, _options.IdleTimeout.TotalMilliseconds / 4));
            using var timer = new PeriodicTimer(period);
            while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var idleFor = DateTime.UtcNow - _lastInboundUtc;
                if (idleFor > _options.IdleTimeout)
                {
                    _options.Logger.IdleTimeoutExceeded(idleFor, _options.IdleTimeout);
                    try { await TerminateAsync(TerminationCode.KeepaliveIntervalLapsed, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* best-effort */ }
                    return;
                }
            }
        }, token);
    }

    private static DateTimeOffset NanosToOffset(ulong unixNanos) =>
        unixNanos == 0UL ? DateTimeOffset.MinValue
                         : DateTimeOffset.UnixEpoch.AddTicks((long)(unixNanos / 100UL));

    private async ValueTask EvaluateRiskAsync(OutboundRequestKind kind, object req, ulong securityId, string clordid, CancellationToken ct)
    {
        if (RiskGates.Count == 0) return;
        var snapshot = new OutboundRequest(kind, req, securityId, clordid);
        foreach (var gate in RiskGates)
        {
            var decision = await gate.EvaluateAsync(snapshot, ct).ConfigureAwait(false);
            if (decision.Kind != RiskDecisionKind.Allow)
            {
                _options.Logger.RiskGateDecision(gate.GetType().Name, decision.Kind.ToString(), decision.Reason);
                EntryPointTelemetry.RiskRejections.Add(1,
                    new KeyValuePair<string, object?>("kind", kind.ToString()),
                    new KeyValuePair<string, object?>("decision", decision.Kind.ToString()));
                throw new RiskRejectedException(decision);
            }
        }
    }

    private static Activity? StartOutbound(string op, OutboundRequestKind kind, ulong securityId, string clordid)
    {
        var act = EntryPointTelemetry.ActivitySource.StartActivity(op, ActivityKind.Client);
        if (act is not null)
        {
            act.SetTag("entrypoint.kind", kind.ToString());
            act.SetTag("entrypoint.security_id", securityId);
            act.SetTag("entrypoint.clordid", clordid);
        }
        return act;
    }

    private static void RecordLatency(long startTs, OutboundRequestKind kind)
    {
        var elapsedMs = (Stopwatch.GetTimestamp() - startTs) * 1000.0 / Stopwatch.Frequency;
        EntryPointTelemetry.OutboundLatency.Record(elapsedMs,
            new KeyValuePair<string, object?>("kind", kind.ToString()));
    }

    private async ValueTask HydrateFromSnapshotAsync(CancellationToken ct)
    {
        if (_options.SessionStateStore is null) return;
        var snapshot = await _options.SessionStateStore.ReplayAsync(ct).ConfigureAwait(false);
        if (snapshot is null) return;
        if (snapshot.SessionId != _options.SessionId)
        {
            _options.Logger.StaleSnapshotIgnored(snapshot.SessionId, _options.SessionId);
            return;
        }
        _session!.ResumeOutboundSeqNum(snapshot.LastOutboundSeqNum + 1UL);
        _lastInboundSeqNum = snapshot.LastInboundSeqNum;
        _outstandingOrders.Clear();
        foreach (var (clordid, secId) in snapshot.OutstandingOrders)
            _outstandingOrders[clordid] = secId;
        _options.Logger.SnapshotRecovered((uint)snapshot.LastOutboundSeqNum, _outstandingOrders.Count);
    }

    private async ValueTask AppendOutboundDeltaAsync(ulong seq, string clordid, ulong securityId, CancellationToken ct)
    {
        var store = _options.SessionStateStore;
        if (store is null) return;
        _outstandingOrders[clordid] = securityId;
        try
        {
            await store.AppendDeltaAsync(new OutboundDelta(seq, clordid, securityId), ct).ConfigureAwait(false);
            await MaybeCompactAsync(store, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Logger.AppendDeltaFailed(ex, clordid);
        }
    }

    private async ValueTask MaybeCompactAsync(ISessionStateStore store, CancellationToken ct)
    {
        var threshold = _options.StateCompactEveryDeltas;
        if (threshold <= 0) return;
        if (System.Threading.Interlocked.Increment(ref _deltasSinceCompact) < threshold) return;
        System.Threading.Interlocked.Exchange(ref _deltasSinceCompact, 0);
        try
        {
            await store.SaveAsync(BuildSnapshot(), ct).ConfigureAwait(false);
            await store.CompactAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Logger.SnapshotCompactionFailed(ex);
        }
    }

    private SessionSnapshot BuildSnapshot() => new()
    {
        SessionId = _options.SessionId,
        SessionVerId = _options.SessionVerId,
        LastOutboundSeqNum = _session is null ? 0UL : _session.LastAssignedOutboundSeqNum(),
        LastInboundSeqNum = _lastInboundSeqNum,
        CapturedAt = DateTimeOffset.UtcNow,
        OutstandingOrders = new Dictionary<string, ulong>(_outstandingOrders),
    };

    private void OnInboundEventForPersistence(EntryPointEvent evt)
    {
        if (evt.SeqNum > _lastInboundSeqNum) _lastInboundSeqNum = evt.SeqNum;
        var store = _options.SessionStateStore;
        if (store is null) return;

        string? closedClOrdId = evt switch
        {
            OrderCancelled c => c.ClOrdID.Value.ToString(),
            OrderRejected r => r.ClOrdID.Value.ToString(),
            OrderTrade t when t.LeavesQty == 0UL => t.ClOrdID.Value.ToString(),
            _ => null,
        };

        if (closedClOrdId is null) return;
        _outstandingOrders.TryRemove(closedClOrdId, out _);

        EnqueuePersistOp(new PersistOp(closedClOrdId, evt.SeqNum));
    }

    /// <summary>
    /// Enqueues a persistence op onto the bounded channel drained by
    /// <see cref="RunPersistenceWorkerAsync"/>. When the channel is saturated
    /// the producer blocks (<see cref="BoundedChannelFullMode.Wait"/>),
    /// intentionally backpressuring the inbound loop so persistence cannot
    /// silently drop close-events. A Trace log is emitted on saturation. (#121)
    /// </summary>
    private void EnqueuePersistOp(PersistOp op)
    {
        var channel = _persistChannel;
        if (channel is null) return;
        try
        {
            if (channel.Writer.TryWrite(op)) return;

            if (_options.Logger.IsEnabled(LogLevel.Trace))
                _options.Logger.PersistenceChannelSaturated(_options.PersistenceQueueCapacity);

            // Block synchronously to backpressure the inbound loop. The
            // inbound dispatcher invokes this callback in-line, so awaiting
            // here is what makes BoundedChannelFullMode.Wait actually surface
            // backpressure all the way back to the wire reader.
            var cts = _persistCts;
            channel.Writer.WriteAsync(op, cts?.Token ?? CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (ChannelClosedException)
        {
            // Worker completed during teardown — drop is expected.
        }
        catch (OperationCanceledException)
        {
            // Persistence CTS cancelled during teardown — drop is expected.
        }
    }

    private void StartPersistenceWorker() => StartPersistenceWorkerCore();

    // Test hooks (internals visible to B3.EntryPoint.Client.Tests) so the
    // persistence worker can be exercised directly without a live FIXP session.
    internal void StartPersistenceWorkerForTesting() => StartPersistenceWorkerCore();
    internal void EnqueuePersistOpForTesting(string clOrdID, ulong inboundSeqNum)
        => EnqueuePersistOp(new PersistOp(clOrdID, inboundSeqNum));
    internal Task StopActiveSessionForTestingAsync(CancellationToken ct = default)
        => StopActiveSessionAsync(ct);

    private void StartPersistenceWorkerCore()
    {
        var store = _options.SessionStateStore;
        if (store is null) return;

        var capacity = Math.Max(1, _options.PersistenceQueueCapacity);
        var channel = Channel.CreateBounded<PersistOp>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var cts = new CancellationTokenSource();
        _persistChannel = channel;
        _persistCts = cts;
        _persistWorker = Task.Run(() => RunPersistenceWorkerAsync(channel.Reader, store, cts.Token));
    }

    private async Task RunPersistenceWorkerAsync(ChannelReader<PersistOp> reader, ISessionStateStore store, CancellationToken ct)
    {
        try
        {
            await foreach (var op in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await store.AppendDeltaAsync(new OrderClosedDelta(op.ClOrdID), ct).ConfigureAwait(false);
                    await store.AppendDeltaAsync(new InboundDelta(op.InboundSeqNum), ct).ConfigureAwait(false);
                    await MaybeCompactAsync(store, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // Log and continue — a transient store failure must not take down the worker.
                    _options.Logger.OrderClosedPersistFailed(ex, op.ClOrdID);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    /// <inheritdoc />
    public async Task<ClOrdID> SubmitAsync(NewOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var clordid = request.ClOrdID.Value.ToString();
        await EvaluateRiskAsync(OutboundRequestKind.NewOrder, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.submit", OutboundRequestKind.NewOrder, request.SecurityId, clordid);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[NewOrderSingleData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeNewOrderSingle(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersSubmitted.Add(1, new KeyValuePair<string, object?>("kind", "NewOrder"));
        RecordLatency(startTs, OutboundRequestKind.NewOrder);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> SubmitSimpleAsync(SimpleNewOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var clordid = request.ClOrdID.Value.ToString();
        await EvaluateRiskAsync(OutboundRequestKind.SimpleNewOrder, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.submit_simple", OutboundRequestKind.SimpleNewOrder, request.SecurityId, clordid);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[SimpleNewOrderData.MESSAGE_SIZE + 64];
        var len = OrderEntryEncoder.EncodeSimpleNewOrder(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersSubmitted.Add(1, new KeyValuePair<string, object?>("kind", "SimpleNewOrder"));
        RecordLatency(startTs, OutboundRequestKind.SimpleNewOrder);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> ReplaceAsync(ReplaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var clordid = request.ClOrdID.Value.ToString();
        await EvaluateRiskAsync(OutboundRequestKind.Replace, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.replace", OutboundRequestKind.Replace, request.SecurityId, clordid);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[OrderCancelReplaceRequestData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeOrderCancelReplace(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersReplaced.Add(1, new KeyValuePair<string, object?>("kind", "Replace"));
        RecordLatency(startTs, OutboundRequestKind.Replace);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> ReplaceSimpleAsync(SimpleModifyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var clordid = request.ClOrdID.Value.ToString();
        await EvaluateRiskAsync(OutboundRequestKind.SimpleReplace, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.replace_simple", OutboundRequestKind.SimpleReplace, request.SecurityId, clordid);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[SimpleModifyOrderData.MESSAGE_SIZE + 64];
        var len = OrderEntryEncoder.EncodeSimpleModifyOrder(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersReplaced.Add(1, new KeyValuePair<string, object?>("kind", "SimpleReplace"));
        RecordLatency(startTs, OutboundRequestKind.SimpleReplace);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task CancelAsync(CancelOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var clordid = request.ClOrdID.Value.ToString();
        await EvaluateRiskAsync(OutboundRequestKind.Cancel, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.cancel", OutboundRequestKind.Cancel, request.SecurityId, clordid);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[OrderCancelRequestData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeOrderCancel(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersCancelled.Add(1);
        RecordLatency(startTs, OutboundRequestKind.Cancel);
    }

    /// <inheritdoc />
    public Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        return SendMassActionWithRiskAsync(request, ct);

        async Task<MassActionReport> SendMassActionWithRiskAsync(MassActionRequest req, CancellationToken token)
        {
            var clordid = req.ClOrdID.Value.ToString();
            await EvaluateRiskAsync(OutboundRequestKind.MassAction, req, req.SecurityId ?? 0UL, clordid, token).ConfigureAwait(false);
            using var activity = StartOutbound("entrypoint.mass_action", OutboundRequestKind.MassAction, req.SecurityId ?? 0UL, clordid);
            var startTs = Stopwatch.GetTimestamp();
            var seq = _session!.NextOutboundSeqNum();
            var buffer = new byte[OrderMassActionRequestData.MESSAGE_SIZE + 32];
            var len = OrderEntryEncoder.EncodeOrderMassAction(buffer, req, _options, seq);
            await _session.SendApplicationFrameAsync(buffer, len, token).ConfigureAwait(false);
            await AppendOutboundDeltaAsync(seq, clordid, req.SecurityId ?? 0UL, token).ConfigureAwait(false);
            EntryPointTelemetry.MassActions.Add(1);
            RecordLatency(startTs, OutboundRequestKind.MassAction);
            return new MassActionReport
            {
                ClOrdID = req.ClOrdID,
                ActionType = req.ActionType,
                Response = B3.EntryPoint.Client.Models.MassActionResponse.Accepted,
                Scope = req.Scope,
                TotalAffectedOrders = 0,
            };
        }
    }

    /// <inheritdoc />
    public async Task<string> SubmitCrossAsync(NewOrderCrossRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        var clordid = request.CrossId;
        await EvaluateRiskAsync(OutboundRequestKind.NewOrder, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.submit_cross", OutboundRequestKind.NewOrder, request.SecurityId, clordid);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var legBytes = (request.Legs?.Count ?? 0) * B3.Entrypoint.Fixp.Sbe.V6.NewOrderCrossData.NoSidesData.MESSAGE_SIZE;
        var buffer = new byte[B3.Entrypoint.Fixp.Sbe.V6.NewOrderCrossData.MESSAGE_SIZE + legBytes + 256];
        var len = OrderEntryEncoder.EncodeNewOrderCross(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersSubmitted.Add(1, new KeyValuePair<string, object?>("kind", "NewOrderCross"));
        RecordLatency(startTs, OutboundRequestKind.NewOrder);
        return request.CrossId;
    }

    /// <inheritdoc />
    public async Task SendQuoteRequestAsync(QuoteRequestMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        await EvaluateRiskAsync(OutboundRequestKind.NewOrder, request, request.SecurityId, request.QuoteReqId, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.quote_request", OutboundRequestKind.NewOrder, request.SecurityId, request.QuoteReqId);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[QuoteRequestData.MESSAGE_SIZE + 32];
        var len = OrderEntryEncoder.EncodeQuoteRequest(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, request.QuoteReqId, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersSubmitted.Add(1, new KeyValuePair<string, object?>("kind", "QuoteRequest"));
        RecordLatency(startTs, OutboundRequestKind.NewOrder);
    }

    /// <inheritdoc />
    public async Task SendQuoteAsync(QuoteMessage quote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(quote);
        EnsureEstablished();
        await EvaluateRiskAsync(OutboundRequestKind.NewOrder, quote, quote.SecurityId, quote.QuoteId, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.quote", OutboundRequestKind.NewOrder, quote.SecurityId, quote.QuoteId);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[QuoteData.MESSAGE_SIZE + 32];
        var len = OrderEntryEncoder.EncodeQuote(buffer, quote, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, quote.QuoteId, quote.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersSubmitted.Add(1, new KeyValuePair<string, object?>("kind", "Quote"));
        RecordLatency(startTs, OutboundRequestKind.NewOrder);
    }

    /// <inheritdoc />
    public async Task CancelQuoteAsync(string quoteId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(quoteId);
        EnsureEstablished();
        await EvaluateRiskAsync(OutboundRequestKind.Cancel, quoteId, 0UL, quoteId, ct).ConfigureAwait(false);
        using var activity = StartOutbound("entrypoint.quote_cancel", OutboundRequestKind.Cancel, 0UL, quoteId);
        var startTs = Stopwatch.GetTimestamp();
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[QuoteCancelData.MESSAGE_SIZE + 32];
        var len = OrderEntryEncoder.EncodeQuoteCancel(buffer, quoteId, 0UL, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, quoteId, 0UL, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersCancelled.Add(1, new KeyValuePair<string, object?>("kind", "QuoteCancel"));
        RecordLatency(startTs, OutboundRequestKind.Cancel);
    }

    /// <summary>
    /// Send a graceful <c>Terminate</c> to the peer and tear down the session.
    /// Records an <c>entrypoint.terminate</c> activity, increments the
    /// terminations counter, and raises the <see cref="Terminated"/> event
    /// before returning.
    /// </summary>
    public async Task TerminateAsync(TerminationCode code, CancellationToken ct = default)
    {
        if (_session is null)
            throw new InvalidOperationException("Client is not connected.");
        using var activity = EntryPointTelemetry.ActivitySource.StartActivity("entrypoint.terminate", ActivityKind.Client);
        activity?.SetTag("entrypoint.terminate_code", code.ToString());
        await _session.TerminateAsync((B3.Entrypoint.Fixp.Sbe.V6.TerminationCode)(byte)code, ct).ConfigureAwait(false);
        EntryPointTelemetry.Terminations.Add(1,
            new KeyValuePair<string, object?>("direction", "outbound"),
            new KeyValuePair<string, object?>("code", code.ToString()));
        RaiseTerminated(code, reason: null, initiatedByClient: true);
    }

    /// <summary>
    /// Snapshot of the client's session health for liveness/readiness probes.
    /// Pure data; consumers (e.g. ASP.NET Core <c>IHealthCheck</c>) decide thresholds.
    /// </summary>
    public ClientHealth GetHealth()
    {
        var last = _lastInboundUtc == default ? DateTime.UtcNow : _lastInboundUtc;
        return new ClientHealth(State, last, DateTime.UtcNow - last);
    }

    /// <summary>
    /// Reconnect against the same logical session bumping
    /// <see cref="EntryPointClientOptions.SessionVerId"/>. The next
    /// <c>SessionVerID</c> must be strictly greater than the previous one;
    /// otherwise the gateway terminates with
    /// <see cref="TerminationCode.InvalidSessionVerId"/>.
    /// </summary>
    public async Task ReconnectAsync(uint nextSessionVerId, CancellationToken ct = default)
    {
        if (nextSessionVerId <= _options.SessionVerId)
            throw new ArgumentOutOfRangeException(nameof(nextSessionVerId),
                "Next SessionVerID must be strictly greater than the current one.");

        // Best-effort graceful Terminate before tearing the active session down.
        try
        {
            if (_session is not null)
                await _session.TerminateAsync(B3.Entrypoint.Fixp.Sbe.V6.TerminationCode.FINISHED, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }

        // Drain background work + dispose transport BEFORE Establishing the new
        // session, so old persistence/idle/keep-alive cannot race with the new
        // one (#124).
        await StopActiveSessionAsync(ct).ConfigureAwait(false);

        _options.SessionVerId = nextSessionVerId;
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Async stream of unsolicited events from the peer
    /// (<see cref="OrderAccepted"/>, <see cref="OrderRejected"/>,
    /// <see cref="OrderTrade"/>, <see cref="OrderCancelled"/>,
    /// <see cref="OrderModified"/>, <see cref="BusinessReject"/>, etc.).
    /// Backed by a bounded channel of capacity
    /// <see cref="EntryPointClientOptions.EventChannelCapacity"/> (default 4096) with
    /// <see cref="BoundedChannelFullMode.Wait"/>: when the channel fills, the inbound
    /// decoder awaits a free slot before publishing the next event. A consumer that
    /// does not drain promptly therefore stalls the decoder, which propagates
    /// backpressure all the way to the wire reader (and ultimately the TCP receive
    /// window). No events are dropped.
    /// </summary>
    public async IAsyncEnumerable<EntryPointEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureEstablished();
        await foreach (var evt in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    public async ValueTask DisposeAsync()
    {
        await StopActiveSessionAsync(CancellationToken.None).ConfigureAwait(false);
        _events.Writer.TryComplete();
    }

    /// <summary>
    /// Centralized teardown for the active session. Cancels session-scoped
    /// cancellation sources, completes the persistence channel writer, awaits
    /// background tasks (idle watchdog + persistence worker) under a hard
    /// timeout (<see cref="EntryPointClientOptions.SessionTeardownTimeout"/>),
    /// then disposes keep-alive, the FIXP session (which awaits its inbound
    /// loop), the TCP transport and the retransmit handler. Shared by
    /// <see cref="ReconnectAsync"/> and <see cref="DisposeAsync"/> so the
    /// same ordering applies in both paths (#124).
    /// </summary>
    private async Task StopActiveSessionAsync(CancellationToken ct)
    {
        var timeout = _options.SessionTeardownTimeout;
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromSeconds(5);

        // 1. Cancel session-scoped CTSs and complete the persistence channel
        //    so its worker drains the in-flight queue and exits.
        try { _idleCts?.Cancel(); } catch { }
        _persistChannel?.Writer.TryComplete();

        // 2. Await background tasks under a hard timeout. Timed-out tasks are
        //    logged and abandoned (the persistence CT is cancelled below to
        //    unblock any I/O it may still be holding).
        await AwaitWithTimeoutAsync("idle-watchdog", _idleWatchdog, timeout).ConfigureAwait(false);
        await AwaitWithTimeoutAsync("persistence-worker", _persistWorker, timeout).ConfigureAwait(false);

        // Cancel the persistence CT after the drain attempt so any store call
        // still in flight after the timeout is force-cancelled.
        try { _persistCts?.Cancel(); } catch { }

        // 3. Dispose transport-layer resources in order:
        //    keep-alive scheduler -> FIXP session (awaits inbound loop) ->
        //    TCP socket. The retransmit handler holds no resources.
        try { _keepAlive?.Dispose(); } catch { }
        if (_session is not null)
        {
            try { await _session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        try { _tcp?.Dispose(); } catch { }

        // 4. Reset references so a subsequent Connect/Reconnect starts clean.
        try { _idleCts?.Dispose(); } catch { }
        try { _persistCts?.Dispose(); } catch { }
        _idleCts = null;
        _idleWatchdog = null;
        _persistChannel = null;
        _persistCts = null;
        _persistWorker = null;
        _keepAlive = null;
        _retransmit = null;
        _session = null;
        _tcp = null;
    }

    private async Task AwaitWithTimeoutAsync(string name, Task? task, TimeSpan timeout)
    {
        if (task is null) return;
        if (task.IsCompleted)
        {
            try { await task.ConfigureAwait(false); } catch { }
            return;
        }
        var winner = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (winner != task)
        {
            _options.Logger.SessionTeardownTimeout(name, timeout);
            return;
        }
        try { await task.ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// Internal hook used by <see cref="TerminateAsync"/> and the inbound
    /// <c>Terminate</c> handler to surface a <see cref="Terminated"/>
    /// notification through the public event.
    /// </summary>
    internal void RaiseTerminated(TerminationCode code, string? reason, bool initiatedByClient) =>
        Terminated?.Invoke(this, new TerminatedEventArgs(code, reason, initiatedByClient));

    private void EnsureEstablished()
    {
        if (_session?.State != FixpClientState.Established)
            throw new InvalidOperationException("Client is not in Established state.");
    }
}
