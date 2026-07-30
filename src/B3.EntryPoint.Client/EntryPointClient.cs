using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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

    // #211: NextOutboundSeqNum() reserves a MsgSeqNum atomically, but the
    // subsequent encode + SendApplicationFrameAsync steps are not otherwise
    // serialized. Two concurrent callers (e.g. "Cancel All" firing several
    // CancelAsync calls) could reserve seq N and N+1, then race to actually
    // write to the wire, letting N+1 land before N. This lock spans
    // reserve-seq -> encode -> write for every outbound application frame so
    // wire order always matches seq-assignment order.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // #208: set by TryColdResumeAsync immediately before it bumps SessionVerID
    // and falls through to a fresh Negotiate (recoverable EstablishReuse
    // reject). Consumed exactly once by the following HydrateFromSnapshotAsync
    // call so it does NOT resume outbound/inbound state from the now-stale
    // (pre-bump) snapshot — a fresh Negotiate starts application sequencing
    // over on both sides regardless of the persisted counters.
    private bool _suppressNextSnapshotResume;
    private KeepAliveScheduler? _keepAlive;
    private RetransmitRequestHandler? _retransmit;
    private DateTime _lastInboundUtc;
    private CancellationTokenSource? _idleCts;
    private Task? _idleWatchdog;
    private Channel<PersistOp>? _persistChannel;
    private CancellationTokenSource? _persistCts;
    private Task? _persistWorker;
    private int _outboundRecoveryRequired;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    private readonly ConcurrentDictionary<ClOrdID, ulong> _outstandingOrders = new();

    /// <summary>
    /// Outstanding non-order outbound IDs (quote/cross flows where the
    /// identifier is an arbitrary <see cref="string"/>, not a numeric
    /// <see cref="ClOrdID"/>). Kept separate so the order-tracking dict can
    /// use the strongly-typed <see cref="ClOrdID"/> key without paying a
    /// per-event <c>ToString()</c> allocation on the close-event hot path
    /// (#128).
    /// </summary>
    private readonly ConcurrentDictionary<string, ulong> _outstandingQuoteFlowIds = new(StringComparer.Ordinal);

    private long _deltasSinceCompact;

    // ---- Inbound gap detection (#138) ---------------------------------------
    // _lastContiguousInboundSeqNum is the last inbound app-frame seq we know
    // arrived contiguously from seq 1 (or from the persisted snapshot's
    // contiguous tail). Persisted into SessionSnapshot.LastInboundSeqNum.
    // _highestInboundSeqNum is the running max of any inbound seq we've seen,
    // including frames that landed past a gap. The two diverge while a gap is
    // outstanding. _pendingInboundSeqs holds the post-gap seqs received so we
    // can advance _lastContiguousInboundSeqNum incrementally as the missing
    // frames arrive (typically via Retransmission). _gapRequestInFlight caps
    // concurrent outstanding RetransmitRequests at one.
    private readonly object _inboundGapGate = new();
    private ulong _lastContiguousInboundSeqNum;
    private ulong _highestInboundSeqNum;
    private readonly SortedSet<ulong> _pendingInboundSeqs = new();
    private bool _gapRequestInFlight;

    // ---- In-retransmission-window tracking (#175) ---------------------------
    // FIXP §4.7: an inbound Retransmission(NextSeqNo, Count) frame promises the
    // peer will deliver exactly Count app frames with seqs
    // [NextSeqNo, NextSeqNo+Count). _activeRetransmission tracks the bounded
    // reply burst so the client can detect protocol violations:
    //   * peer over- or under-delivers vs declared Count,
    //   * peer interleaves a new bounded reply before completing the previous,
    //   * peer sends a non-retransmitted (live) seq while the reply is open.
    // None of these conditions is fatal — they're logged at Warning and the
    // contiguity tracker above keeps doing the right thing — but they're
    // observable regressions a wire-puro client should not silently absorb.
    private RetransmissionWindow? _activeRetransmission;

    private readonly record struct RetransmissionWindow(
        ulong NextSeqNo,
        uint Count,
        uint Received,
        ulong ExpectedSeq,
        bool Completed,
        ulong RequestNanos)
    {
        public ulong LastSeqInWindow => NextSeqNo + Count - 1UL;
        public bool Contains(ulong seq) => seq >= NextSeqNo && seq <= LastSeqInWindow;
    }

    /// <summary>
    /// Persistence operation enqueued from the inbound loop and drained by a
    /// single dedicated worker per session lifetime. Replaces the legacy
    /// fire-and-forget <c>Task.Run</c> in
    /// <see cref="OnInboundEventForPersistence"/> so persistence work is
    /// tracked and deterministically drained on teardown (#121). Carries a
    /// strongly-typed <see cref="ClOrdID"/> to avoid a per-event string
    /// allocation (#128).
    /// </summary>
    private readonly record struct PersistOp(ClOrdID ClOrdID, ulong InboundSeqNum);

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

    /// <summary>
    /// Raised exactly once per <see cref="ReconnectAsync(uint, System.Threading.CancellationToken)"/> call when the prior
    /// session terminated with an unrecovered inbound app-frame gap. The peer
    /// bumps <c>SessionVerID</c> on reconnect and resets its outbound counter
    /// to 1, so the missing range cannot be served by a §4.7 <c>RetransmitRequest</c>
    /// against the new session. Consumers should reconcile out-of-band (e.g.
    /// via a business-layer order-status query). (#138)
    /// </summary>
    public event EventHandler<InboundGapAtReconnectEventArgs>? InboundGapAtReconnect;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_session is not null)
            throw new InvalidOperationException("Client is already connected.");

        // #191: cold-start (process-restart) session resume. Attempt Establish
        // reusing the persisted SessionVerID before falling back to a fresh
        // Negotiate, so venue-side order ownership survives an OMS restart.
        // Attempted at most ONCE per ConnectAsync, before the Negotiate retry
        // loop, so a transient Negotiate failure never re-loads the snapshot
        // and rewinds the SessionVerID (#191 review).
        if (_options.ConnectMode == ConnectMode.EstablishReuseThenNegotiate &&
            await TryColdResumeAsync(ct).ConfigureAwait(false))
        {
            return;
        }

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
        try
        {
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

            await FinishEstablishedAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Any failure after the TCP connect has succeeded leaves a partially
            // initialized session: _tcp, _session (maybe), the persistence
            // worker, keep-alive and inbound loop may all be live. Without this
            // cleanup, the outer ConnectAsync retry loop would create a brand
            // new TcpClient and orphan everything started here. StopActive-
            // SessionAsync is idempotent and null-safe across all these fields,
            // and intentionally does NOT complete _events.Writer (#144) so the
            // event channel survives across reconnect attempts.
            await StopActiveSessionAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Runs everything that must happen after a successful <c>Establish</c>
    /// (whether full Negotiate+Establish via <see cref="ConnectOnceAsync"/> or
    /// spec-canonical Establish-reuse via
    /// <see cref="ReconnectAsync(ReconnectMode, System.Func{uint, uint}?, CancellationToken)"/>):
    /// snapshot hydration, persistence-worker start, inbound-loop start,
    /// keep-alive scheduler + retransmit handler wiring (#173).
    /// </summary>
    private async Task FinishEstablishedAsync(CancellationToken ct)
    {
        await HydrateFromSnapshotAsync(ct).ConfigureAwait(false);

        // #152: persist the session identity (SessionId/SessionVerId) at
        // lifecycle boundaries, not on a delta count. Writing immediately
        // after a successful Establish guarantees `snapshot.json` exists
        // even if the host restarts before `StateCompactEveryDeltas`
        // appends accumulate, so warm-restart and Reconnect can resolve
        // the verId the peer actually accepted.
        await PersistSnapshotAsync(ct).ConfigureAwait(false);

        StartPersistenceWorker();

        _session!.OnInboundEvent = OnInboundEventForPersistence;

        var establishedSession = _session;
        _keepAlive = CreateKeepAliveScheduler(establishedSession, SendKeepAliveSequenceAsync);
        _session.OnInboundSequence = nextSeq =>
        {
            _lastInboundUtc = DateTime.UtcNow;
            _keepAlive!.RaiseFrameReceived(nextSeq, DateTimeOffset.UtcNow);
        };

        _retransmit = new RetransmitRequestHandler(
            sendRequest: (from, count, token) => _session.SendRetransmitRequestAsync(from, count, token));
        _session.OnInboundRetransmission = (nextSeq, count, reqNanos) =>
        {
            _lastInboundUtc = DateTime.UtcNow;
            // The peer's reply to our outstanding RetransmitRequest. Whether
            // it carries frames (count>0) or is an empty completion (count=0,
            // observed against TestPeer / some gateway error paths), we clear
            // the in-flight flag so a future gap can issue a new request.
            // The retransmitted frames themselves flow through the inbound
            // loop and OnInboundEventForPersistence advances the contiguous
            // tail accordingly. (#138)
            lock (_inboundGapGate)
            {
                _gapRequestInFlight = false;
                // #175: install / replace the bounded-reply window so the
                // contiguity tracker can count frames against the declared
                // Count and surface peer protocol violations.
                if (count > 0)
                {
                    if (_activeRetransmission is { } prior && !prior.Completed)
                    {
                        _options.Logger.RetransmissionWindowUnderDelivered(
                            prior.NextSeqNo, prior.Count, prior.Received);
                        _options.Logger.RetransmissionWindowOverlapped(
                            prior.NextSeqNo, prior.Count, prior.Received,
                            nextSeq, count);
                    }
                    _activeRetransmission = new RetransmissionWindow(
                        NextSeqNo: nextSeq,
                        Count: count,
                        Received: 0u,
                        ExpectedSeq: nextSeq,
                        Completed: false,
                        RequestNanos: reqNanos);
                }
                else
                {
                    // count==0 is a no-op completion; clear any stale window
                    // so we don't keep accounting against a phantom reply.
                    _activeRetransmission = null;
                }
            }
            _retransmit!.RaiseRetransmissionReceived(nextSeq, count, NanosToOffset(reqNanos));
        };
        _session.OnInboundRetransmitReject = (code, reqNanos) =>
        {
            _lastInboundUtc = DateTime.UtcNow;
            // Reject clears the in-flight flag — a later gap may need to
            // re-request after a transient peer-side condition lifts. (#138)
            // It also tears down any in-progress retransmission window
            // because the peer is no longer going to deliver the declared
            // Count frames. (#175)
            lock (_inboundGapGate)
            {
                _gapRequestInFlight = false;
                _activeRetransmission = null;
            }
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
        _session.OnTransportClosed = ex =>
        {
            // Peer closed the TCP transport without a Terminate exchange
            // (e.g. matching-platform restart). Surface as a Terminated event
            // so the State property and EnsureEstablished() reflect the dead
            // wire instead of leaving consumers stuck in a stale Established
            // state until the next send fails. (#187)
            EntryPointTelemetry.Terminations.Add(1,
                new KeyValuePair<string, object?>("direction", "inbound"),
                new KeyValuePair<string, object?>("code", "transport-closed"));
            RaiseTerminated(
                TerminationCode.Unspecified,
                reason: ex?.Message ?? "TCP transport closed by peer.",
                initiatedByClient: false);
        };

        // Start the inbound loop LAST — only after every On* callback
        // (including `_retransmit` and the retransmission handlers above) is
        // wired. Otherwise a gap frame arriving in the setup window could hit
        // OnInboundEventForPersistence while `_retransmit` is still null, latch
        // `_gapRequestInFlight = true` without issuing a request, and
        // permanently suppress both the live and the cold-resume proactive
        // retransmit (#191 review).
        //
        // Start keep-alive only after OnTransportClosed is wired as well. A
        // first-tick send failure must reach the outer Terminated event rather
        // than only transitioning the inner session state.
        _keepAlive.Start();
        _session.StartInboundLoop(_events.Writer);

        _lastInboundUtc = DateTime.UtcNow;
    }

    private KeepAliveScheduler CreateKeepAliveScheduler(
        FixpClientSession session,
        Func<CancellationToken, Task<ulong>> sendSequence)
    {
        var sessionId = _options.SessionId;
        var sessionVerId = _options.SessionVerId;
        var endpoint = _options.Endpoint;
        return new KeepAliveScheduler(
            _options.KeepAliveInterval,
            sendSequence,
            onTiming: timing =>
            {
                var tags = new TagList
                {
                    { "session.id", sessionId },
                    { "session.ver_id", sessionVerId },
                    { "server.address", endpoint.Address.ToString() },
                    { "server.port", endpoint.Port },
                };
                EntryPointTelemetry.KeepAliveSchedulingDelay.Record(
                    timing.SchedulingDelay.TotalMilliseconds, tags);
                EntryPointTelemetry.KeepAliveSendDuration.Record(
                    timing.SendDuration.TotalMilliseconds, tags);
            },
            onFailure: failure =>
            {
                _options.Logger.KeepAliveFaulted(
                    failure.Exception,
                    sessionId,
                    sessionVerId,
                    endpoint,
                    failure.Kind,
                    failure.SchedulingDelay,
                    failure.SendDuration);
                EntryPointTelemetry.KeepAliveFailures.Add(1,
                    new KeyValuePair<string, object?>("session.id", sessionId),
                    new KeyValuePair<string, object?>("session.ver_id", sessionVerId),
                    new KeyValuePair<string, object?>("server.address", endpoint.Address.ToString()),
                    new KeyValuePair<string, object?>("server.port", endpoint.Port),
                    new KeyValuePair<string, object?>("kind", failure.Kind.ToString()));
                session.FaultTransport(failure.Exception);
            });
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
                    try { await TerminateAsync(TerminationCode.KeepaliveIntervalLapsed, token).ConfigureAwait(false); }
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
        if (_suppressNextSnapshotResume)
        {
            _suppressNextSnapshotResume = false;
            // See field doc on _suppressNextSnapshotResume (#208): a cold-start
            // EstablishReuse reject just bumped SessionVerID and fell through
            // to a fresh Negotiate — the snapshot's counters belong to the
            // rejected (pre-bump) SessionVerID and must not be resumed.
            _options.Logger.StaleSnapshotSessionVerIdIgnored(snapshot.SessionVerId, _options.SessionVerId, _options.SessionId);
            return;
        }
        _session!.ResumeOutboundSeqNum(snapshot.LastOutboundSeqNum + 1UL);
        lock (_inboundGapGate)
        {
            _lastContiguousInboundSeqNum = snapshot.LastInboundSeqNum;
            _highestInboundSeqNum = snapshot.LastInboundSeqNum;
            _pendingInboundSeqs.Clear();
            _gapRequestInFlight = false;
            _activeRetransmission = null;
        }
        _outstandingOrders.Clear();
        _outstandingQuoteFlowIds.Clear();
        foreach (var (idText, secId) in snapshot.OutstandingOrders)
        {
            // ClOrdID is a uint64 wire type. Numeric keys hydrate into the typed
            // order dict; arbitrary strings (quote/cross IDs) hydrate into the
            // string-keyed quote-flow dict. (#128)
            if (ulong.TryParse(idText, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var ord) && ord != 0UL)
                _outstandingOrders[new ClOrdID(ord)] = secId;
            else
                _outstandingQuoteFlowIds[idText] = secId;
        }
        _options.Logger.SnapshotRecovered((uint)snapshot.LastOutboundSeqNum,
            _outstandingOrders.Count + _outstandingQuoteFlowIds.Count);
    }

    private async ValueTask AppendOutboundOrderDeltaAsync(ulong seq, ClOrdID clordid, string clordidText, ulong securityId, CancellationToken ct)
    {
        var store = _options.SessionStateStore;
        if (store is null) return;
        _outstandingOrders[clordid] = securityId;
        try
        {
            await store.AppendDeltaAsync(new OutboundDelta(seq, clordidText, securityId), ct).ConfigureAwait(false);
            await MaybeCompactAsync(store, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _options.Logger.AppendDeltaFailed(ex, clordidText);
        }
    }

    private async ValueTask AppendOutboundDeltaAsync(ulong seq, string clordid, ulong securityId, CancellationToken ct)
    {
        var store = _options.SessionStateStore;
        if (store is null) return;
        _outstandingQuoteFlowIds[clordid] = securityId;
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

    /// <summary>
    /// Best-effort persistence of the current <see cref="SessionSnapshot"/>
    /// at a lifecycle boundary (post-Establish, pre-teardown). Unlike
    /// <see cref="MaybeCompactAsync"/> this is unconditional — it does not
    /// gate on <see cref="EntryPointClientOptions.StateCompactEveryDeltas"/>
    /// — and is therefore the mechanism that makes <c>SessionId</c> /
    /// <c>SessionVerId</c> durable across restarts even when fewer than
    /// <c>StateCompactEveryDeltas</c> deltas have been appended (#152).
    /// Failures are logged via <see cref="LogMessages.SnapshotCompactionFailed"/>
    /// and swallowed: persistence is advisory and must never break the
    /// connect or teardown path.
    /// </summary>
    private async ValueTask PersistSnapshotAsync(CancellationToken ct)
    {
        var store = _options.SessionStateStore;
        if (store is null || _session is null) return;
        try
        {
            await store.SaveAsync(BuildSnapshot(), ct).ConfigureAwait(false);
            // SaveAsync subsumes the delta log; reset the in-memory counter
            // so the next MaybeCompactAsync threshold is measured from here.
            System.Threading.Interlocked.Exchange(ref _deltasSinceCompact, 0);
        }
        catch (Exception ex)
        {
            _options.Logger.SnapshotCompactionFailed(ex);
        }
    }

    private SessionSnapshot BuildSnapshot()
    {
        ulong contiguous;
        lock (_inboundGapGate) { contiguous = _lastContiguousInboundSeqNum; }
        // Merge the typed order dict and the string-keyed quote-flow dict into
        // a single string-keyed serialization shape for backward compat with
        // pre-#128 SessionSnapshot wire format. ClOrdID.ToString() runs only
        // here (snapshot/compact boundary), not on the per-event hot path.
        var outstanding = new Dictionary<string, ulong>(_outstandingOrders.Count + _outstandingQuoteFlowIds.Count);
        foreach (var kv in _outstandingOrders)
            outstanding[kv.Key.ToString()] = kv.Value;
        foreach (var kv in _outstandingQuoteFlowIds)
            outstanding[kv.Key] = kv.Value;
        return new SessionSnapshot
        {
            SessionId = _options.SessionId,
            SessionVerId = _options.SessionVerId,
            LastOutboundSeqNum = _session is null ? 0UL : _session.LastAssignedOutboundSeqNum(),
            // #138: persist the last *contiguous* inbound seq, never the
            // running max. A snapshot captured while a gap is outstanding
            // would otherwise silently lose the missing range on resume.
            LastInboundSeqNum = contiguous,
            CapturedAt = DateTimeOffset.UtcNow,
            OutstandingOrders = outstanding,
        };
    }

    private void OnInboundEventForPersistence(EntryPointEvent evt)
    {
        ulong contiguousAfter;
        bool requestGap = false;
        ulong gapFrom = 0UL;
        uint gapCount = 0u;
        ulong gapTriggerSeq = 0UL;
        ulong gapExpectedSeq = 0UL;

        lock (_inboundGapGate)
        {
            var seq = evt.SeqNum;
            if (seq > _highestInboundSeqNum) _highestInboundSeqNum = seq;

            // #175: account this frame against any active retransmission
            // window before contiguity bookkeeping. Per §4.7 the burst is
            // strictly ordered: peer must send seqs NextSeqNo, NextSeqNo+1,
            // ..., NextSeqNo+Count-1 — exactly Count frames, one per seq.
            // Any deviation (duplicate inside burst, missing seq, seq out of
            // range, more frames than declared) is a peer protocol violation
            // we log but do not act on beyond that. The window stays open
            // after reaching Count (Completed=true) so we can distinguish a
            // legitimate next-live frame at NextSeqNo+Count from an extra
            // in-range frame (genuine over-delivery).
            if (_activeRetransmission is { } window)
            {
                if (!window.Completed && seq == window.ExpectedSeq)
                {
                    var nextExpected = window.ExpectedSeq + 1UL;
                    var received = window.Received + 1u;
                    var completed = received >= window.Count;
                    if (completed)
                    {
                        _options.Logger.RetransmissionWindowCompleted(
                            window.NextSeqNo, window.Count);
                    }
                    _activeRetransmission = window with
                    {
                        Received = received,
                        ExpectedSeq = nextExpected,
                        Completed = completed,
                    };
                }
                else if (window.Contains(seq))
                {
                    // Seq is inside the declared range. Two flavors:
                    //   * window not yet Completed → duplicate or
                    //     out-of-order (logged as out-of-sequence).
                    //   * window already Completed → genuine over-delivery
                    //     (peer sent an extra frame in the bounded range
                    //     after fulfilling the declared count).
                    if (window.Completed)
                    {
                        _options.Logger.RetransmissionWindowOverDelivered(
                            window.NextSeqNo, window.Count, seq);
                    }
                    else
                    {
                        _options.Logger.RetransmissionFrameOutOfSequence(
                            window.NextSeqNo, window.Count, window.Received,
                            window.ExpectedSeq, seq);
                    }
                }
                else
                {
                    // Seq is outside [NextSeqNo, NextSeqNo+Count). Two cases:
                    //   * window not Completed → peer terminated the burst
                    //     early; under-delivery.
                    //   * window Completed → this is normal next-live
                    //     traffic; close silently.
                    if (!window.Completed)
                    {
                        _options.Logger.RetransmissionWindowUnderDelivered(
                            window.NextSeqNo, window.Count, window.Received);
                    }
                    _activeRetransmission = null;
                }
            }

            if (seq == _lastContiguousInboundSeqNum + 1UL)
            {
                _lastContiguousInboundSeqNum = seq;
                // Drain any post-gap seqs that are now contiguous.
                while (_pendingInboundSeqs.Count > 0)
                {
                    var min = _pendingInboundSeqs.Min;
                    if (min != _lastContiguousInboundSeqNum + 1UL) break;
                    _lastContiguousInboundSeqNum = min;
                    _pendingInboundSeqs.Remove(min);
                }
                if (_lastContiguousInboundSeqNum >= _highestInboundSeqNum)
                    _gapRequestInFlight = false;
            }
            else if (seq > _lastContiguousInboundSeqNum + 1UL)
            {
                // Gap. Buffer the post-gap seq for later contiguity tracking;
                // the frame itself is still surfaced to consumers immediately
                // by the inbound loop (any duplicates that arrive via the
                // Retransmission reply are detected as already-contiguous and
                // ignored for advancement, but still flow to consumers). #138
                _pendingInboundSeqs.Add(seq);
                if (!_gapRequestInFlight)
                {
                    gapFrom = _lastContiguousInboundSeqNum + 1UL;
                    var missing = seq - gapFrom;
                    gapCount = (uint)Math.Min(missing, RetransmitRequestHandler.MaxCountPerRequest);
                    gapExpectedSeq = gapFrom;
                    gapTriggerSeq = seq;
                    _gapRequestInFlight = true;
                    requestGap = true;
                }
            }
            // else: duplicate (seq <= _lastContiguousInboundSeqNum). Already
            // contiguous; persistence below is still safe — it operates on
            // ClOrdID set membership.

            contiguousAfter = _lastContiguousInboundSeqNum;
        }

        if (requestGap)
        {
            _options.Logger.InboundGapDetected(gapExpectedSeq, gapTriggerSeq, gapFrom, gapCount);
            var handler = _retransmit;
            if (handler is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await handler.RequestRetransmitAsync(gapFrom, gapCount).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Reset in-flight on send failure so a later frame can
                        // re-trigger the request.
                        lock (_inboundGapGate) { _gapRequestInFlight = false; }
                        _options.Logger.InboundGapRequestFailed(ex, gapFrom, gapCount);
                    }
                });
            }
        }

        var store = _options.SessionStateStore;
        if (store is null) return;

        // Strongly-typed close key — ClOrdID is a readonly record struct
        // wrapping a ulong, so this carries no allocation. (#128)
        ClOrdID? closedClOrdId = evt switch
        {
            OrderCancelled c => c.ClOrdID,
            OrderRejected r => r.ClOrdID,
            OrderTrade t when t.LeavesQty == 0UL => t.ClOrdID,
            _ => null,
        };

        if (closedClOrdId is null) return;
        _outstandingOrders.TryRemove(closedClOrdId.Value, out _);

        // #138: persist the contiguous tail, not the raw evt.SeqNum, so the
        // replayed snapshot's LastInboundSeqNum matches BuildSnapshot semantics.
        EnqueuePersistOp(new PersistOp(closedClOrdId.Value, contiguousAfter));
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
    internal void EnqueuePersistOpForTesting(ClOrdID clOrdID, ulong inboundSeqNum)
        => EnqueuePersistOp(new PersistOp(clOrdID, inboundSeqNum));
    internal Task StopActiveSessionForTestingAsync(CancellationToken ct = default)
        => StopActiveSessionAsync(ct);

    internal void HandleInboundEventForTesting(EntryPointEvent evt) => OnInboundEventForPersistence(evt);
    internal void BindRetransmitForTesting(RetransmitRequestHandler handler) => _retransmit = handler;
    internal bool HasActiveSessionForTesting => _session is not null || _tcp is not null;
    internal ulong PeekNextOutboundSeqNumForTesting() => _session?.PeekNextOutboundSeqNum() ?? 0UL;
    internal Task<ulong> SendKeepAliveSequenceForTestingAsync(CancellationToken ct = default) =>
        SendKeepAliveSequenceAsync(ct);
    internal void StartKeepAliveForTesting(Func<CancellationToken, Task<ulong>> sendSequence)
    {
        var session = _session ?? throw new InvalidOperationException("No test session is attached.");
        _session.OnTransportClosed = ex => RaiseTerminated(
            TerminationCode.Unspecified,
            ex?.Message ?? "TCP transport closed.",
            initiatedByClient: false);
        _keepAlive = CreateKeepAliveScheduler(session, sendSequence);
        _keepAlive.Start();
    }
    internal void AttachEstablishedSessionForTesting(Stream stream)
    {
        _session = new FixpClientSession(stream, _options);
        _session.ForceEstablishedForTesting();
    }
    internal (ulong contiguous, ulong highest, int pending, bool gapInFlight) GetInboundGapStateForTesting()
    {
        lock (_inboundGapGate)
            return (_lastContiguousInboundSeqNum, _highestInboundSeqNum, _pendingInboundSeqs.Count, _gapRequestInFlight);
    }

    // #175
    internal void OpenRetransmissionWindowForTesting(ulong nextSeqNo, uint count, ulong requestNanos = 0UL)
    {
        lock (_inboundGapGate)
        {
            if (count == 0u)
            {
                _activeRetransmission = null;
                return;
            }
            if (_activeRetransmission is { } prior && !prior.Completed)
            {
                _options.Logger.RetransmissionWindowUnderDelivered(
                    prior.NextSeqNo, prior.Count, prior.Received);
                _options.Logger.RetransmissionWindowOverlapped(
                    prior.NextSeqNo, prior.Count, prior.Received,
                    nextSeqNo, count);
            }
            _activeRetransmission = new RetransmissionWindow(nextSeqNo, count, 0u, nextSeqNo, false, requestNanos);
        }
    }

    internal (ulong nextSeqNo, uint count, uint received, bool completed)? GetRetransmissionWindowForTesting()
    {
        lock (_inboundGapGate)
            return _activeRetransmission is { } w ? (w.NextSeqNo, w.Count, w.Received, w.Completed) : null;
    }
    internal void RaiseInboundGapAtReconnectForTesting(ulong fromSeqNo, uint count, ulong priorSessionVerId)
        => InboundGapAtReconnect?.Invoke(this, new InboundGapAtReconnectEventArgs(fromSeqNo, count, priorSessionVerId));
    internal void SetInboundGapStateForTesting(ulong contiguous, ulong highest)
    {
        lock (_inboundGapGate)
        {
            _lastContiguousInboundSeqNum = contiguous;
            _highestInboundSeqNum = highest;
        }
    }

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
                    _options.Logger.OrderClosedPersistFailed(ex, op.ClOrdID.ToString());
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    /// <summary>
    /// Reserves the next outbound MsgSeqNum, encodes the application frame via
    /// <paramref name="encode"/>, and writes it to the session — all under
    /// <see cref="_sendLock"/> (#211) so the reserve→encode→write critical
    /// section for concurrent outbound calls (e.g. a burst of CancelAsync from
    /// a "Cancel All" action) can never interleave. This guarantees the wire
    /// order of application frames always matches the order in which their
    /// MsgSeqNum was assigned.
    /// </summary>
    private async Task<ulong> SendOrderEntryFrameAsync(
        Func<ulong, (byte[] Buffer, int Length)> encode, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOutboundSessionUsable();
            var seq = _session!.NextOutboundSeqNum();
            var (buffer, len) = encode(seq);
            await _session.SendApplicationFrameAsync(buffer, len, ct).ConfigureAwait(false);
            return seq;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<OutboundAttemptReceipt> SendOrderEntryFrameWithReceiptAsync(
        OutboundOperationKind operation,
        ClOrdID clOrdId,
        ulong securityId,
        Func<ulong, (byte[] Buffer, int Length)> encode,
        OutboundFramePreparedCallback onFramePrepared,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(onFramePrepared);

        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OutboundAttemptException(
                "The outbound attempt ended before a sequence number was reserved.",
                OutboundAttemptStage.NotStarted,
                null,
                ex);
        }

        ulong seq = 0;
        FixpClientSession? session = null;
        OutboundFrameIdentity? identity = null;
        var stage = OutboundAttemptStage.NotStarted;
        try
        {
            EnsureOutboundSessionUsable();
            session = _session!;
            seq = session.NextOutboundSeqNum();
            stage = OutboundAttemptStage.SequenceReserved;
            var (buffer, length) = encode(seq);
            identity = new OutboundFrameIdentity(
                _options.SessionId,
                _options.SessionVerId,
                seq,
                operation,
                clOrdId,
                length,
                Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, length))));
            stage = OutboundAttemptStage.SequenceReservedAndEncoded;

            try
            {
                await onFramePrepared(identity, ct).ConfigureAwait(false);
                stage = OutboundAttemptStage.FramePrepared;
            }
            catch (Exception ex)
            {
                session.TryRollbackUnwrittenOutboundSeqNum(seq);
                throw new OutboundAttemptException(
                    "The frame-prepared callback failed; no transport write was attempted.",
                    stage,
                    identity,
                    ex);
            }

            try
            {
                if (!ReferenceEquals(_session, session))
                    throw new InvalidOperationException("The FIXP session changed before the prepared frame could be written.");
                await session.SendApplicationFrameWithEvidenceAsync(buffer, length, ct).ConfigureAwait(false);
                stage = OutboundAttemptStage.TransportWriteCompleted;
            }
            catch (ApplicationFrameWriteException ex)
            {
                if (!ex.TransportWriteCompleted)
                    Volatile.Write(ref _outboundRecoveryRequired, 1);
                var failedStage = ex.TransportWriteCompleted
                    ? OutboundAttemptStage.TransportWriteCompleted
                    : OutboundAttemptStage.TransportWriteStarted;
                throw new OutboundAttemptException(
                    ex.TransportWriteCompleted
                        ? "The transport write completed, but the transport flush failed."
                        : "The transport write started but did not complete; its wire outcome is indeterminate.",
                    failedStage,
                    identity,
                    ex.InnerException ?? ex);
            }
            catch (Exception ex)
            {
                if (stage >= OutboundAttemptStage.FramePrepared)
                    Volatile.Write(ref _outboundRecoveryRequired, 1);
                if (stage == OutboundAttemptStage.SequenceReserved)
                    session.TryRollbackUnwrittenOutboundSeqNum(seq);
                throw new OutboundAttemptException(
                    "The attempt ended before the transport write started.",
                    stage,
                    identity,
                    ex);
            }

            try
            {
                await AppendOutboundOrderDeltaStrictAsync(
                    seq, clOrdId, clOrdId.Value.ToString(), securityId, ct).ConfigureAwait(false);
                if (_options.SessionStateStore is not null)
                    stage = OutboundAttemptStage.SdkSessionStatePersisted;
            }
            catch (Exception ex)
            {
                throw new OutboundAttemptException(
                    "The transport write completed, but SDK session-state persistence failed.",
                    stage,
                    identity,
                    ex);
            }

            return new OutboundAttemptReceipt(identity, stage);
        }
        catch (OutboundAttemptException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (stage == OutboundAttemptStage.SequenceReserved)
                session?.TryRollbackUnwrittenOutboundSeqNum(seq);
            throw new OutboundAttemptException(
                "The outbound attempt ended before the frame-prepared callback completed.",
                stage,
                identity,
                ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<ulong> SendKeepAliveSequenceAsync(CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = _session ?? throw new InvalidOperationException("Client is not connected.");
            var seq = session.PeekNextOutboundSeqNum();
            await session.SendSequenceAsync(seq, ct).ConfigureAwait(false);
            return seq;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void EnsureEstablishedForAttempt()
    {
        try
        {
            EnsureEstablished();
        }
        catch (Exception ex)
        {
            throw new OutboundAttemptException(
                "The outbound attempt cannot start because the FIXP session is not established.",
                OutboundAttemptStage.NotStarted,
                null,
                ex);
        }
    }

    private void EnsureOutboundSessionUsable()
    {
        if (Volatile.Read(ref _outboundRecoveryRequired) != 0)
            throw new InvalidOperationException(
                "The prior prepared outbound attempt has an indeterminate wire outcome. " +
                "Reconcile it and reconnect with a fresh SessionVerId before sending another application frame.");
    }

    private async ValueTask EvaluateRiskForAttemptAsync(
        OutboundRequestKind kind,
        object request,
        ulong securityId,
        string clOrdId,
        CancellationToken ct)
    {
        try
        {
            await EvaluateRiskAsync(kind, request, securityId, clOrdId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OutboundAttemptException(
                "The outbound attempt ended during pre-trade evaluation.",
                OutboundAttemptStage.NotStarted,
                null,
                ex);
        }
    }

    private async ValueTask AppendOutboundOrderDeltaStrictAsync(
        ulong seq,
        ClOrdID clOrdId,
        string clOrdIdText,
        ulong securityId,
        CancellationToken ct)
    {
        var store = _options.SessionStateStore;
        if (store is null)
            return;

        _outstandingOrders[clOrdId] = securityId;
        await store.AppendDeltaAsync(new OutboundDelta(seq, clOrdIdText, securityId), ct).ConfigureAwait(false);
        await MaybeCompactStrictAsync(store, ct).ConfigureAwait(false);
    }

    private async ValueTask MaybeCompactStrictAsync(ISessionStateStore store, CancellationToken ct)
    {
        var threshold = _options.StateCompactEveryDeltas;
        if (threshold <= 0)
            return;
        if (Interlocked.Increment(ref _deltasSinceCompact) < threshold)
            return;

        Interlocked.Exchange(ref _deltasSinceCompact, 0);
        await store.SaveAsync(BuildSnapshot(), ct).ConfigureAwait(false);
        await store.CompactAsync(ct).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var buffer = new byte[NewOrderSingleData.MESSAGE_SIZE + 256];
            var len = OrderEntryEncoder.EncodeNewOrderSingle(buffer, request, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
        await AppendOutboundOrderDeltaAsync(seq, request.ClOrdID, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersSubmitted.Add(1, new KeyValuePair<string, object?>("kind", "NewOrder"));
        RecordLatency(startTs, OutboundRequestKind.NewOrder);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<OutboundAttemptReceipt> SubmitWithReceiptAsync(
        NewOrderRequest request,
        OutboundFramePreparedCallback onFramePrepared,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onFramePrepared);
        EnsureEstablishedForAttempt();
        var clordid = request.ClOrdID.Value.ToString();
        await EvaluateRiskForAttemptAsync(
            OutboundRequestKind.NewOrder, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        return await SendOrderEntryFrameWithReceiptAsync(
            OutboundOperationKind.NewOrder,
            request.ClOrdID,
            request.SecurityId,
            s =>
            {
                var buffer = new byte[NewOrderSingleData.MESSAGE_SIZE + 256];
                var len = OrderEntryEncoder.EncodeNewOrderSingle(buffer, request, _options, s);
                return (buffer, len);
            },
            onFramePrepared,
            ct).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var buffer = new byte[SimpleNewOrderData.MESSAGE_SIZE + 64];
            var len = OrderEntryEncoder.EncodeSimpleNewOrder(buffer, request, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
        await AppendOutboundOrderDeltaAsync(seq, request.ClOrdID, clordid, request.SecurityId, ct).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var buffer = new byte[OrderCancelReplaceRequestData.MESSAGE_SIZE + 256];
            var len = OrderEntryEncoder.EncodeOrderCancelReplace(buffer, request, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
        await AppendOutboundOrderDeltaAsync(seq, request.ClOrdID, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersReplaced.Add(1, new KeyValuePair<string, object?>("kind", "Replace"));
        RecordLatency(startTs, OutboundRequestKind.Replace);
        return request.ClOrdID;
    }

    /// <inheritdoc />
    public async Task<OutboundAttemptReceipt> ReplaceWithReceiptAsync(
        ReplaceOrderRequest request,
        OutboundFramePreparedCallback onFramePrepared,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onFramePrepared);
        EnsureEstablishedForAttempt();
        var clordid = request.ClOrdID.Value.ToString();
        await EvaluateRiskForAttemptAsync(
            OutboundRequestKind.Replace, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        return await SendOrderEntryFrameWithReceiptAsync(
            OutboundOperationKind.Replace,
            request.ClOrdID,
            request.SecurityId,
            s =>
            {
                var buffer = new byte[OrderCancelReplaceRequestData.MESSAGE_SIZE + 256];
                var len = OrderEntryEncoder.EncodeOrderCancelReplace(buffer, request, _options, s);
                return (buffer, len);
            },
            onFramePrepared,
            ct).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var buffer = new byte[SimpleModifyOrderData.MESSAGE_SIZE + 64];
            var len = OrderEntryEncoder.EncodeSimpleModifyOrder(buffer, request, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
        await AppendOutboundOrderDeltaAsync(seq, request.ClOrdID, clordid, request.SecurityId, ct).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var buffer = new byte[OrderCancelRequestData.MESSAGE_SIZE + 256];
            var len = OrderEntryEncoder.EncodeOrderCancel(buffer, request, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
        await AppendOutboundOrderDeltaAsync(seq, request.ClOrdID, clordid, request.SecurityId, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersCancelled.Add(1);
        RecordLatency(startTs, OutboundRequestKind.Cancel);
    }

    /// <inheritdoc />
    public async Task<OutboundAttemptReceipt> CancelWithReceiptAsync(
        CancelOrderRequest request,
        OutboundFramePreparedCallback onFramePrepared,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onFramePrepared);
        EnsureEstablishedForAttempt();
        var clordid = request.ClOrdID.Value.ToString();
        await EvaluateRiskForAttemptAsync(
            OutboundRequestKind.Cancel, request, request.SecurityId, clordid, ct).ConfigureAwait(false);
        return await SendOrderEntryFrameWithReceiptAsync(
            OutboundOperationKind.Cancel,
            request.ClOrdID,
            request.SecurityId,
            s =>
            {
                var buffer = new byte[OrderCancelRequestData.MESSAGE_SIZE + 256];
                var len = OrderEntryEncoder.EncodeOrderCancel(buffer, request, _options, s);
                return (buffer, len);
            },
            onFramePrepared,
            ct).ConfigureAwait(false);
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
            var seq = await SendOrderEntryFrameAsync(s =>
            {
                var buffer = new byte[OrderMassActionRequestData.MESSAGE_SIZE + 32];
                var len = OrderEntryEncoder.EncodeOrderMassAction(buffer, req, _options, s);
                return (buffer, len);
            }, token).ConfigureAwait(false);
            await AppendOutboundOrderDeltaAsync(seq, req.ClOrdID, clordid, req.SecurityId ?? 0UL, token).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var legBytes = (request.Legs?.Count ?? 0) * B3.Entrypoint.Fixp.Sbe.V6.NewOrderCrossData.NoSidesData.MESSAGE_SIZE;
            var buffer = new byte[B3.Entrypoint.Fixp.Sbe.V6.NewOrderCrossData.MESSAGE_SIZE + legBytes + 256];
            var len = OrderEntryEncoder.EncodeNewOrderCross(buffer, request, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var buffer = new byte[QuoteRequestData.MESSAGE_SIZE + 32];
            var len = OrderEntryEncoder.EncodeQuoteRequest(buffer, request, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var buffer = new byte[QuoteData.MESSAGE_SIZE + 32];
            var len = OrderEntryEncoder.EncodeQuote(buffer, quote, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
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
        var seq = await SendOrderEntryFrameAsync(s =>
        {
            var buffer = new byte[QuoteCancelData.MESSAGE_SIZE + 32];
            var len = OrderEntryEncoder.EncodeQuoteCancel(buffer, quoteId, 0UL, _options, s);
            return (buffer, len);
        }, ct).ConfigureAwait(false);
        await AppendOutboundDeltaAsync(seq, quoteId, 0UL, ct).ConfigureAwait(false);
        EntryPointTelemetry.OrdersCancelled.Add(1, new KeyValuePair<string, object?>("kind", "QuoteCancel"));
        RecordLatency(startTs, OutboundRequestKind.Cancel);
    }

    /// <summary>
    /// Flushes any bytes buffered by the underlying transport. Pair with
    /// <see cref="EntryPointClientOptions.AutoFlushOutboundFrames"/> = <c>false</c>
    /// at batch boundaries; safe to call when not connected (no-op). Issue #123.
    /// </summary>
    public Task FlushAsync(CancellationToken ct = default) =>
        _session is { } s ? s.FlushOutboundAsync(ct) : Task.CompletedTask;

    /// <summary>
    /// Send a graceful <c>Terminate</c> to the peer and tear down the session.
    /// Records an <c>entrypoint.terminate</c> activity, increments the
    /// terminations counter, and raises the <see cref="Terminated"/> event
    /// before returning.
    /// </summary>
    public async Task TerminateAsync(TerminationCode code, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
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
        finally
        {
            _sendLock.Release();
        }
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
    /// <remarks>
    /// Legacy v0.14.3 overload — equivalent to calling the new
    /// <see cref="ReconnectAsync(ReconnectMode, System.Func{uint, uint}?, CancellationToken)"/>
    /// with <see cref="ReconnectMode.AlwaysNegotiate"/> and a constant
    /// selector. Prefer the new overload when transient TCP drops should
    /// reattach without burning a <c>SessionVerID</c> (#173).
    /// </remarks>
#pragma warning disable RS0027 // optional-param overload predates the longer #173 overload; kept for source compat
    public async Task ReconnectAsync(uint nextSessionVerId, CancellationToken ct = default)
#pragma warning restore RS0027
    {
        if (nextSessionVerId <= _options.SessionVerId)
            throw new ArgumentOutOfRangeException(nameof(nextSessionVerId),
                "Next SessionVerID must be strictly greater than the current one.");

        await ReconnectAsync(ReconnectMode.AlwaysNegotiate, _ => nextSessionVerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Spec-canonical reconnect (#173). On
    /// <see cref="ReconnectMode.EstablishReuseThenNegotiate"/>, sends
    /// <c>Establish</c> reusing the current <c>SessionVerID</c> first; falls
    /// back to <c>Negotiate</c> with a strictly-greater <c>SessionVerID</c>
    /// (chosen via <paramref name="nextSessionVerIdSelector"/>) only when
    /// <c>Establish</c> is rejected for a recoverable reason. On
    /// <see cref="ReconnectMode.AlwaysNegotiate"/>, behaves like the legacy
    /// <see cref="ReconnectAsync(uint, CancellationToken)"/> overload —
    /// always rotates the <c>SessionVerID</c>. The returned
    /// <see cref="ReconnectOutcome"/> surfaces which branch was taken plus
    /// the peer's <c>EstablishmentAck</c> counters when relevant.
    /// </summary>
    /// <param name="mode">Reconnect strategy. See <see cref="ReconnectMode"/>.</param>
    /// <param name="nextSessionVerIdSelector">
    /// Invoked by the SDK <em>only when</em> a fresh <c>Negotiate</c> is
    /// actually required (Establish rejected on the reuse path, or
    /// <see cref="ReconnectMode.AlwaysNegotiate"/> mode). Receives the
    /// previous <c>SessionVerID</c> and must return a strictly-greater value
    /// (e.g. the spec's recommended microseconds-since-epoch). When
    /// <see langword="null"/>, defaults to <c>prev =&gt; prev + 1</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
#pragma warning disable RS0026, RS0027 // intentional overload introduced in #173; legacy retains optional ct
    public async Task<ReconnectOutcome> ReconnectAsync(
        ReconnectMode mode,
        Func<uint, uint>? nextSessionVerIdSelector,
        CancellationToken ct)
#pragma warning restore RS0026, RS0027
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Stop the heartbeat producer as soon as this deliberate session
            // swap owns the outbound lock. In particular, do this before the
            // best-effort Terminate write below so a queued heartbeat cannot
            // consume its liveness budget waiting for this same lock and
            // falsely report a transport fault during reconnect.
            try { _keepAlive?.Stop(); } catch { }

            var selector = nextSessionVerIdSelector ?? (static prev => checked(prev + 1));

            // #138: capture an outstanding inbound gap from the prior session
            // before any state is reset. On the Renegotiated path the peer bumps
            // SessionVerID and resets its outbound to 1, so the missing range is
            // unrecoverable in-band — surface it via InboundGapAtReconnect after
            // the new session is up so consumers can reconcile out-of-band.
            // On the Reattached path the gap is recoverable via §4.7 and the
            // returned ReconnectOutcome.RetransmitWindowReady advertises that.
            ulong gapFrom = 0UL;
            uint gapCount = 0u;
            var priorSessionVerId = (ulong)_options.SessionVerId;
            ulong localLastAssignedOutbound = _session?.LastAssignedOutboundSeqNum() ?? 0UL;
            ulong localLastContiguousInbound;
            lock (_inboundGapGate)
            {
                localLastContiguousInbound = _lastContiguousInboundSeqNum;
                if (_highestInboundSeqNum > _lastContiguousInboundSeqNum)
                {
                    gapFrom = _lastContiguousInboundSeqNum + 1UL;
                    var window = _highestInboundSeqNum - _lastContiguousInboundSeqNum;
                    gapCount = (uint)(window - (ulong)_pendingInboundSeqs.Count);
                }
            }

            // Best-effort graceful Terminate before tearing the active session
            // down — ONLY when deliberately rotating to a new logical session
            // (AlwaysNegotiate). For EstablishReuseThenNegotiate we intend to
            // reattach the SAME session, so a Terminate(Finished) here would tell
            // the venue the session is done-for-good and evict order ownership
            // before the reattach (#191). The old transport is torn down by
            // StopActiveSessionAsync below in either case.
            if (mode == ReconnectMode.AlwaysNegotiate)
            {
                try
                {
                    if (_session is not null)
                        await _session.TerminateAsync(B3.Entrypoint.Fixp.Sbe.V6.TerminationCode.FINISHED, ct).ConfigureAwait(false);
                }
                catch { /* best-effort */ }
            }

            // The transport teardown below must not itself emit a
            // Terminate(Finished) via session dispose: for AlwaysNegotiate we just
            // sent one explicitly above; for EstablishReuseThenNegotiate we intend
            // to reattach the same session and must NOT evict ownership (#191).
            await StopActiveSessionAsync(ct, suppressTerminate: true).ConfigureAwait(false);

            ReconnectOutcome outcome;
            if (mode == ReconnectMode.EstablishReuseThenNegotiate)
            {
                outcome = await TryReattachOrRenegotiateAsync(selector, localLastAssignedOutbound, localLastContiguousInbound, ct).ConfigureAwait(false);
            }
            else
            {
                outcome = await RenegotiateAsync(selector, ct).ConfigureAwait(false);
            }

            // Surface a Renegotiated-path inbound gap (Reattach paths recover
            // in-band via RetransmitWindowReady — no need to also raise the
            // out-of-band event, the application can drive a RetransmitRequest
            // directly from the outcome).
            if (gapCount > 0u && outcome.Kind == ReconnectKind.Renegotiated)
            {
                _options.Logger.InboundGapAtReconnect(priorSessionVerId, gapFrom, gapCount);
                InboundGapAtReconnect?.Invoke(this,
                    new InboundGapAtReconnectEventArgs(gapFrom, gapCount, priorSessionVerId));
            }

            return outcome;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// True when the gateway should retain the session across a TCP drop and
    /// can therefore answer a reused-SessionVerID Establish with
    /// EstablishmentAck. UNNEGOTIATED / INVALID_SESSIONID /
    /// INVALID_SESSIONVERID / ALREADY_ESTABLISHED / SESSION_BLOCKED /
    /// ESTABLISH_ATTEMPTS_EXCEEDED all indicate the peer cannot reattach
    /// against this SessionVerID but a fresh Negotiate may succeed; every
    /// other reject code is a hard failure that bumping SessionVerID alone
    /// will not fix.
    /// </summary>
    private static bool IsRecoverableEstablishReject(B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode code) => code switch
    {
        B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.UNNEGOTIATED => true,
        B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.ALREADY_ESTABLISHED => true,
        B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.SESSION_BLOCKED => true,
        B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.INVALID_SESSIONID => true,
        B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.INVALID_SESSIONVERID => true,
        B3.Entrypoint.Fixp.Sbe.V6.EstablishRejectCode.ESTABLISH_ATTEMPTS_EXCEEDED => true,
        _ => false,
    };

    private async Task<ReconnectOutcome> TryReattachOrRenegotiateAsync(
        Func<uint, uint> selector,
        ulong localLastAssignedOutbound,
        ulong localLastContiguousInbound,
        CancellationToken ct)
    {
        // -- Reattach attempt: TCP reconnect + Establish (no Negotiate) -----
        var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(_options.Endpoint.Address, _options.Endpoint.Port, connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        Fixp.EstablishReuseResult reuse;
        try
        {
            var transportStream = await EstablishTransportStreamAsync(tcp, ct).ConfigureAwait(false);
            _session = new FixpClientSession(transportStream, _options);

            using (var establish = EntryPointTelemetry.ActivitySource.StartActivity("entrypoint.establish.reuse", ActivityKind.Client))
            {
                // Spec §4.5: Establish's NextSeqNo is the client's next
                // outbound app MsgSeqNum. On reattach (#173) that's the prior
                // session's last assigned + 1, NOT 1 — sending 1 would look
                // like a sequence rewind to the gateway and corrupt the
                // peer-side LastIncomingSeqNo accounting.
                reuse = await _session.EstablishReuseAsync(localLastAssignedOutbound + 1UL > uint.MaxValue
                    ? throw new InvalidOperationException("Outbound sequence overflowed uint range.")
                    : (uint)(localLastAssignedOutbound + 1UL), ct).ConfigureAwait(false);
            }

            if (reuse.Accepted)
            {
                var ack = reuse.Ack!.Value;
                _options.Logger.Established(_options.Endpoint);

                // The new FixpClientSession was just constructed with its
                // outbound counter at 0. On reattach the application sequence
                // continues across the TCP blip, so the new session must
                // resume from `localLastAssignedOutbound + 1` BEFORE any app
                // frame is sent. HydrateFromSnapshotAsync (invoked inside
                // FinishEstablishedAsync) only seeds from a SessionStateStore;
                // when no store is configured, the in-memory counter would
                // otherwise restart at 1 and the next app send would rewind
                // MsgSeqNum on the wire.
                _session!.ResumeOutboundSeqNum(localLastAssignedOutbound + 1UL);

                await FinishEstablishedAsync(ct).ConfigureAwait(false);
                StartIdleWatchdog();

                // Detect a gap in either direction relative to the peer's view.
                // Inbound gap: peer reports it will send seq >= NextSeqNo, but
                // the client's contiguous tail is below NextSeqNo - 1 (i.e.
                // some inbound frames between localLastContiguousInbound+1 and
                // NextSeqNo-1 were lost and the peer can re-deliver them).
                // Outbound gap: peer confirms only up to LastIncomingSeqNo,
                // and the client has assigned strictly more than that — the
                // unconfirmed tail must be re-sent.
                var inboundGap = ack.NextSeqNo > localLastContiguousInbound + 1UL;
                var outboundGap = localLastAssignedOutbound > ack.LastIncomingSeqNo;
                var retransmitWindowReady = inboundGap || outboundGap;

                return new ReconnectOutcome(
                    Kind: ReconnectKind.Reattached,
                    SessionVerId: _options.SessionVerId,
                    ServerNextSeqNoExpected: ack.NextSeqNo,
                    ServerLastIncomingSeqNoSeen: ack.LastIncomingSeqNo,
                    RetransmitWindowReady: retransmitWindowReady);
            }
        }
        catch
        {
            // Reuse attempt blew up (transport / decode / unexpected frame).
            // The current _session is the freshly-built reuse session whose
            // counters are still at 0; do NOT persist a snapshot from it
            // because that would clobber the good snapshot saved when the
            // prior live session was torn down before reattach. Suppress the
            // dispose-time Terminate — this is an internal reattach teardown.
            await StopActiveSessionAsync(CancellationToken.None, persistSnapshot: false, suppressTerminate: true).ConfigureAwait(false);
            throw;
        }

        // Reuse rejected — tear down without persisting (same reason as above)
        // and decide on the fallback path.
        await StopActiveSessionAsync(CancellationToken.None, persistSnapshot: false, suppressTerminate: true).ConfigureAwait(false);

        var code = reuse.RejectCode!.Value;
        if (!IsRecoverableEstablishReject(code))
            throw new Fixp.FixpEstablishRejectedException(code);

        _options.Logger.EstablishReuseRejected(code);
        return await RenegotiateAsync(selector, ct).ConfigureAwait(false);
    }

    private async Task<ReconnectOutcome> RenegotiateAsync(Func<uint, uint> selector, CancellationToken ct)
    {
        var prev = _options.SessionVerId;
        var next = selector(prev);
        if (next <= prev)
            throw new InvalidOperationException(
                $"nextSessionVerIdSelector returned {next} which is not strictly greater than the current SessionVerId {prev}.");

        // Reset inbound seq tracking — the new session starts at peer seq 1.
        // HydrateFromSnapshotAsync (called from the next ConnectAsync) will
        // overwrite this from the persisted snapshot for warm-restart paths.
        lock (_inboundGapGate)
        {
            _lastContiguousInboundSeqNum = 0UL;
            _highestInboundSeqNum = 0UL;
            _pendingInboundSeqs.Clear();
            _gapRequestInFlight = false;
            _activeRetransmission = null;
        }

        _options.SessionVerId = next;
        await ConnectAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _outboundRecoveryRequired, 0);

        return new ReconnectOutcome(
            Kind: ReconnectKind.Renegotiated,
            SessionVerId: next,
            ServerNextSeqNoExpected: 0UL,
            ServerLastIncomingSeqNoSeen: 0UL,
            RetransmitWindowReady: false);
    }

    /// <summary>
    /// #191 cold-start (process-restart) session resume. Attempts to reattach
    /// the previously-negotiated session — TCP reconnect + Establish reusing
    /// the persisted SessionVerID (no Negotiate) — so the venue keeps order
    /// ownership across an OMS restart. Mirrors
    /// <see cref="TryReattachOrRenegotiateAsync"/> but is seeded from the
    /// persisted snapshot instead of a live in-process session.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the session was reattached and the client is
    /// fully connected (the caller must return without Negotiating);
    /// <see langword="false"/> when there is no usable snapshot, or the peer
    /// rejected Establish for a recoverable reason and the caller should fall
    /// through to a fresh Negotiate (the SessionVerID has been bumped in that
    /// case).
    /// </returns>
    private async Task<bool> TryColdResumeAsync(CancellationToken ct)
    {
        var store = _options.SessionStateStore;
        if (store is null)
            return false;

        var snapshot = await store.ReplayAsync(ct).ConfigureAwait(false);
        if (snapshot is null)
            return false;

        // A snapshot from a different SessionId, or one that never reached a
        // negotiated SessionVerID, cannot be reattached — fall through to the
        // normal Negotiate path under the configured SessionVerID.
        if (snapshot.SessionId != _options.SessionId || snapshot.SessionVerId == 0u)
            return false;

        // Establish.NextSeqNo (the client's next outbound app MsgSeqNum) is a
        // uint on the wire; refuse to resume rather than wrap.
        if (snapshot.LastOutboundSeqNum + 1UL > uint.MaxValue)
            return false;

        var nextOutbound = (uint)(snapshot.LastOutboundSeqNum + 1UL);

        // Resume the SAME logical session: reuse the persisted SessionVerID,
        // do NOT bump it. The selector is only consulted on the recoverable
        // reject path below.
        _options.SessionVerId = snapshot.SessionVerId;

        var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(_options.ConnectTimeout);
            await tcp.ConnectAsync(_options.Endpoint.Address, _options.Endpoint.Port, connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        _tcp = tcp;
        Fixp.EstablishReuseResult reuse;
        try
        {
            var transportStream = await EstablishTransportStreamAsync(tcp, ct).ConfigureAwait(false);
            _session = new FixpClientSession(transportStream, _options);

            using (var establish = EntryPointTelemetry.ActivitySource.StartActivity("entrypoint.establish.coldresume", ActivityKind.Client))
            {
                reuse = await _session.EstablishReuseAsync(nextOutbound, ct).ConfigureAwait(false);
            }

            if (reuse.Accepted)
            {
                var ack = reuse.Ack!.Value;
                _options.Logger.Established(_options.Endpoint);

                // FinishEstablishedAsync -> HydrateFromSnapshotAsync re-reads
                // this same snapshot and resumes the outbound counter to
                // LastOutboundSeqNum+1 and the inbound contiguity tail to
                // LastInboundSeqNum, so the cross-restart application sequence
                // is continuous. Resume explicitly first to mirror the live
                // reattach path and stay correct even if hydration is skipped.
                _session!.ResumeOutboundSeqNum(snapshot.LastOutboundSeqNum + 1UL);

                await FinishEstablishedAsync(ct).ConfigureAwait(false);
                StartIdleWatchdog();

                _options.Logger.ColdResumeReattached(_options.SessionId, _options.SessionVerId, nextOutbound);

                // If the peer advertises an inbound gap (it will deliver seq
                // numbers beyond our persisted contiguous tail), proactively
                // request retransmission. The live inbound-gap detector only
                // fires when a post-gap frame arrives; on a fresh reattach the
                // venue may report the gap in the Ack and then send nothing
                // until we ask.
                MaybeRequestColdResumeInboundGap(ack.NextSeqNo, snapshot.LastInboundSeqNum);

                return true;
            }
        }
        catch
        {
            // Reuse blew up (transport / decode). The current _session is the
            // freshly-built reuse session whose counters are at 0; do NOT
            // persist a snapshot from it — that would clobber the good
            // persisted snapshot we just loaded. Rethrow so the host retries
            // ConnectAsync; the SessionVerID stays at the persisted value.
            // Suppress the dispose-time Terminate (internal teardown).
            await StopActiveSessionAsync(CancellationToken.None, persistSnapshot: false, suppressTerminate: true).ConfigureAwait(false);
            throw;
        }

        // Reuse rejected — tear down without persisting and decide. Suppress
        // the dispose-time Terminate (internal teardown).
        await StopActiveSessionAsync(CancellationToken.None, persistSnapshot: false, suppressTerminate: true).ConfigureAwait(false);

        var code = reuse.RejectCode!.Value;
        if (!IsRecoverableEstablishReject(code))
            throw new Fixp.FixpEstablishRejectedException(code);

        // Recoverable: bump the SessionVerID via the validated selector and let
        // the caller fall through to a fresh Negotiate.
        var selector = _options.NextSessionVerIdSelector ?? (static prev => prev + 1u);
        var prevVerId = _options.SessionVerId;
        var nextVerId = selector(prevVerId);
        if (nextVerId <= prevVerId)
            throw new InvalidOperationException(
                $"NextSessionVerIdSelector returned {nextVerId} which is not strictly greater than the persisted SessionVerId {prevVerId}.");

        _options.Logger.ColdResumeFallbackToNegotiate(code, prevVerId);
        _options.SessionVerId = nextVerId;
        // #208: the fresh Negotiate about to happen starts application
        // sequencing over; the snapshot we just loaded belongs to the
        // rejected, pre-bump SessionVerID and must not seed the new session's
        // outbound/inbound counters.
        _suppressNextSnapshotResume = true;
        return false;
    }

    /// <summary>
    /// On a successful cold reattach, issue a single §4.7 RetransmitRequest when
    /// the peer's Ack advertises inbound frames past our persisted contiguous
    /// tail. Reuses the fire-and-forget pattern of the live inbound-gap
    /// detector (<see cref="OnInboundEventForPersistence"/>).
    /// </summary>
    private void MaybeRequestColdResumeInboundGap(ulong ackNextSeqNo, ulong lastContiguousInbound)
    {
        if (ackNextSeqNo <= lastContiguousInbound + 1UL)
            return;

        ulong gapFrom;
        uint gapCount;
        lock (_inboundGapGate)
        {
            if (_gapRequestInFlight)
                return;
            gapFrom = lastContiguousInbound + 1UL;
            var missing = ackNextSeqNo - gapFrom;
            gapCount = (uint)Math.Min(missing, RetransmitRequestHandler.MaxCountPerRequest);
            _gapRequestInFlight = true;
        }

        _options.Logger.InboundGapDetected(gapFrom, ackNextSeqNo, gapFrom, gapCount);
        var handler = _retransmit;
        if (handler is null)
        {
            lock (_inboundGapGate) { _gapRequestInFlight = false; }
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await handler.RequestRetransmitAsync(gapFrom, gapCount).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_inboundGapGate) { _gapRequestInFlight = false; }
                _options.Logger.InboundGapRequestFailed(ex, gapFrom, gapCount);
            }
        });
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

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_disposeGate)
            disposeTask = _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        await _sendLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopActiveSessionAsync(CancellationToken.None).ConfigureAwait(false);
            _events.Writer.TryComplete();
        }
        finally
        {
            _sendLock.Release();
            _sendLock.Dispose();
        }
    }

    /// <summary>
    /// Centralized teardown for the active session. Cancels session-scoped
    /// cancellation sources, completes the persistence channel writer, awaits
    /// background tasks (idle watchdog + persistence worker) under a hard
    /// timeout (<see cref="EntryPointClientOptions.SessionTeardownTimeout"/>),
    /// then disposes keep-alive, the FIXP session (which awaits its inbound
    /// loop), the TCP transport and the retransmit handler. Shared by
    /// <see cref="ReconnectAsync(uint, System.Threading.CancellationToken)"/> and <see cref="DisposeAsync"/> so the
    /// same ordering applies in both paths (#124).
    /// </summary>
    private async Task StopActiveSessionAsync(CancellationToken ct, bool persistSnapshot = true, bool suppressTerminate = false)
    {
        var timeout = _options.SessionTeardownTimeout;
        if (timeout <= TimeSpan.Zero) timeout = TimeSpan.FromSeconds(5);

        // 0. Stop scheduling new heartbeats before teardown work. Reconnect
        //    holds _sendLock while it calls this method; leaving the scheduler
        //    active would make its queued send exceed the liveness budget and
        //    falsely fault a session that is being deliberately replaced.
        try { _keepAlive?.Stop(); } catch { }

        // 1. Flush a final snapshot before tearing down. This must happen
        //    while `_session` is still live (BuildSnapshot reads
        //    `_session.LastAssignedOutboundSeqNum()`) and before the persist
        //    CTS below is cancelled. Guarantees the on-disk snapshot is
        //    contiguous with the last in-memory state on graceful shutdown
        //    and on Reconnect (#152).
        //
        //    #173 caller note: pass persistSnapshot=false when tearing down
        //    a transient Establish-reuse session that never reached
        //    Established — snapshotting its zeroed counters would corrupt
        //    the good snapshot saved from the prior live session.
        if (persistSnapshot)
            await PersistSnapshotAsync(ct).ConfigureAwait(false);

        // 2. Cancel session-scoped CTSs and complete the persistence channel
        //    so its worker drains the in-flight queue and exits.
        try { _idleCts?.Cancel(); } catch { }
        _persistChannel?.Writer.TryComplete();

        // 3. Await background tasks under a hard timeout. Timed-out tasks are
        //    logged and abandoned (the persistence CT is cancelled below to
        //    unblock any I/O it may still be holding).
        await AwaitWithTimeoutAsync("idle-watchdog", _idleWatchdog, timeout).ConfigureAwait(false);
        await AwaitWithTimeoutAsync("persistence-worker", _persistWorker, timeout).ConfigureAwait(false);

        // Cancel the persistence CT after the drain attempt so any store call
        // still in flight after the timeout is force-cancelled.
        try { _persistCts?.Cancel(); } catch { }

        // 4. Dispose transport-layer resources in order:
        //    keep-alive scheduler -> FIXP session (awaits inbound loop) ->
        //    TCP socket. The retransmit handler holds no resources.
        try { _keepAlive?.Dispose(); } catch { }
        if (_session is not null)
        {
            // Internal teardowns (within-process reconnect / cold-resume
            // fallback) must not emit a Terminate(Finished) — we are about to
            // re-open the session, and a Terminate would evict venue-side order
            // ownership (#191). Graceful client dispose leaves this false so
            // EntryPointClientOptions.TerminateOnDispose governs as usual.
            if (suppressTerminate)
                _session.SuppressTerminateOnDispose = true;
            try { await _session.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        try { _tcp?.Dispose(); } catch { }

        // 5. Reset references so a subsequent Connect/Reconnect starts clean.
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
