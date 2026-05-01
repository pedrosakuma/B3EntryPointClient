using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.Risk;
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
public sealed class EntryPointClient : IAsyncDisposable, ISubmitOrder, IReplaceOrder, ICancelOrder
{
    private readonly EntryPointClientOptions _options;
    private readonly Channel<EntryPointEvent> _events =
        Channel.CreateUnbounded<EntryPointEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
    private TcpClient? _tcp;
    private FixpClientSession? _session;
    private KeepAliveScheduler? _keepAlive;
    private RetransmitRequestHandler? _retransmit;
    private DateTime _lastInboundUtc;
    private CancellationTokenSource? _idleCts;
    private Task? _idleWatchdog;

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
        _options = options;
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
                _options.Logger.LogInformation("EntryPointClient connected on attempt {Attempt}/{Max} to {Endpoint}",
                    attempt, attempts, _options.Endpoint);
                StartIdleWatchdog();
                return;
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                lastError = ex;
                var delay = ComputeBackoff(attempt);
                _options.Logger.LogWarning(ex, "ConnectAsync attempt {Attempt}/{Max} failed; retrying in {DelayMs} ms",
                    attempt, attempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        throw lastError ?? new InvalidOperationException("ConnectAsync failed without exception.");
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
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
        _session = new FixpClientSession(tcp.GetStream(), _options);

        await _session.NegotiateAsync(ct).ConfigureAwait(false);
        _options.Logger.LogDebug("FIXP Negotiated with {Endpoint}", _options.Endpoint);
        await _session.EstablishAsync(ct).ConfigureAwait(false);
        _options.Logger.LogDebug("FIXP Established with {Endpoint}", _options.Endpoint);
        _session.StartInboundLoop(_events.Writer);

        _keepAlive = new KeepAliveScheduler(
            _options.KeepAliveInterval,
            sendSequence: (seq, token) => _session.SendSequenceAsync(seq, token),
            nextSeqNo: () => _session.NextOutboundSeqNum());
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
            _options.Logger.LogInformation("Inbound Terminate received: code={Code}", code);
            RaiseTerminated((TerminationCode)code, reason: null, initiatedByClient: false);
        };
        _lastInboundUtc = DateTime.UtcNow;
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
                    _options.Logger.LogWarning("Idle timeout exceeded ({Idle} > {Threshold}); closing session",
                        idleFor, _options.IdleTimeout);
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
                _options.Logger.LogWarning("Risk gate {Gate} {Kind}: {Reason}", gate.GetType().Name, decision.Kind, decision.Reason);
                throw new RiskRejectedException(decision);
            }
        }
    }

    /// <inheritdoc />
    public async Task<ClOrdID> SubmitAsync(NewOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        await EvaluateRiskAsync(OutboundRequestKind.NewOrder, request, request.SecurityId, request.ClOrdID.Value.ToString(), ct).ConfigureAwait(false);
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[NewOrderSingleData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeNewOrderSingle(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> SubmitSimpleAsync(SimpleNewOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        await EvaluateRiskAsync(OutboundRequestKind.SimpleNewOrder, request, request.SecurityId, request.ClOrdID.Value.ToString(), ct).ConfigureAwait(false);
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[SimpleNewOrderData.MESSAGE_SIZE + 64];
        var len = OrderEntryEncoder.EncodeSimpleNewOrder(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> ReplaceAsync(ReplaceOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        await EvaluateRiskAsync(OutboundRequestKind.Replace, request, request.SecurityId, request.ClOrdID.Value.ToString(), ct).ConfigureAwait(false);
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[OrderCancelReplaceRequestData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeOrderCancelReplace(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<ClOrdID> ReplaceSimpleAsync(SimpleModifyRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        await EvaluateRiskAsync(OutboundRequestKind.SimpleReplace, request, request.SecurityId, request.ClOrdID.Value.ToString(), ct).ConfigureAwait(false);
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[SimpleModifyOrderData.MESSAGE_SIZE + 64];
        var len = OrderEntryEncoder.EncodeSimpleModifyOrder(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task CancelAsync(CancelOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        await EvaluateRiskAsync(OutboundRequestKind.Cancel, request, request.SecurityId, request.ClOrdID.Value.ToString(), ct).ConfigureAwait(false);
        var seq = _session!.NextOutboundSeqNum();
        var buffer = new byte[OrderCancelRequestData.MESSAGE_SIZE + 256];
        var len = OrderEntryEncoder.EncodeOrderCancel(buffer, request, _options, seq);
        await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<MassActionReport> MassActionAsync(MassActionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEstablished();
        return SendMassActionWithRiskAsync(request, ct);

        async Task<MassActionReport> SendMassActionWithRiskAsync(MassActionRequest req, CancellationToken token)
        {
            await EvaluateRiskAsync(OutboundRequestKind.MassAction, req, req.SecurityId ?? 0UL, req.ClOrdID.Value.ToString(), token).ConfigureAwait(false);
            var seq = _session!.NextOutboundSeqNum();
            var buffer = new byte[OrderMassActionRequestData.MESSAGE_SIZE + 32];
            var len = OrderEntryEncoder.EncodeOrderMassAction(buffer, req, _options, seq);
            await _session.SendApplicationFrameAsync(buffer, len, token).ConfigureAwait(false);
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

    /// <summary>
    /// Send a graceful <c>Terminate</c> to the peer and tear down the session.
    /// API surface only — the wire-level encode lands in a follow-up PR (issue #6).
    /// </summary>
    public async Task TerminateAsync(TerminationCode code, CancellationToken ct = default)
    {
        if (_session is null)
            throw new InvalidOperationException("Client is not connected.");
        await _session.TerminateAsync((B3.Entrypoint.Fixp.Sbe.V6.TerminationCode)(byte)code, ct).ConfigureAwait(false);
        RaiseTerminated(code, reason: null, initiatedByClient: true);
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

        // Tear down current session/socket if any, then bump SessionVerID and re-handshake.
        try
        {
            if (_session is not null)
                await _session.TerminateAsync(B3.Entrypoint.Fixp.Sbe.V6.TerminationCode.FINISHED, ct).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        _keepAlive?.Dispose();
        _keepAlive = null;
        _retransmit = null;
        if (_session is not null)
            await _session.DisposeAsync().ConfigureAwait(false);
        _tcp?.Dispose();
        _session = null;
        _tcp = null;

        _options.SessionVerId = nextSessionVerId;
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Async stream of unsolicited events (ER/Reject/BusinessReject). The
    /// typed event family lands with issue #9; today the stream completes
    /// immediately.
    /// </summary>
    public async IAsyncEnumerable<EntryPointEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureEstablished();
        await foreach (var evt in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return evt;
    }

    public async ValueTask DisposeAsync()
    {
        try { _idleCts?.Cancel(); } catch { }
        if (_idleWatchdog is not null)
        {
            try { await _idleWatchdog.ConfigureAwait(false); } catch { }
        }
        _idleCts?.Dispose();
        _idleCts = null;
        _idleWatchdog = null;
        _keepAlive?.Dispose();
        _keepAlive = null;
        if (_session is not null)
            await _session.DisposeAsync().ConfigureAwait(false);
        _tcp?.Dispose();
        _session = null;
        _tcp = null;
    }

    /// <summary>
    /// Test/internal hook — surface a <see cref="Terminated"/> notification
    /// through the public event. Replaced by the real wire-level handler in
    /// the follow-up implementation PR.
    /// </summary>
    internal void RaiseTerminated(TerminationCode code, string? reason, bool initiatedByClient) =>
        Terminated?.Invoke(this, new TerminatedEventArgs(code, reason, initiatedByClient));

    private void EnsureEstablished()
    {
        if (_session?.State != FixpClientState.Established)
            throw new InvalidOperationException("Client is not in Established state.");
    }
}
