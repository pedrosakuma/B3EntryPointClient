using System.Net;
using Microsoft.Extensions.Logging;
using B3.EntryPoint.Client.Fixp;

namespace B3.EntryPoint.Client.Logging;

/// <summary>
/// Centralized, source-generated logger messages for B3.EntryPoint.Client.
///
/// EventId ranges (per <see cref="LogLevel"/>):
/// <list type="bullet">
/// <item>1000–1999 Trace (per-frame)</item>
/// <item>2000–2999 Debug (state transitions, decisions)</item>
/// <item>3000–3999 Information (lifecycle)</item>
/// <item>4000–4999 Warning (retries, idle, BusinessReject, NotApplied)</item>
/// <item>5000–5999 Error (terminal failures, unhandled exceptions)</item>
/// </list>
/// Templates are intentionally stable — assert against EventId, not message text.
/// </summary>
internal static partial class LogMessages
{
    // ---------------- Trace (1000–1999) ----------------

    [LoggerMessage(EventId = 1000, Level = LogLevel.Trace,
        Message = "FIXP outbound frame template={TemplateId} length={Length}")]
    public static partial void OutboundFrame(this ILogger logger, int templateId, int length);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Trace,
        Message = "FIXP inbound frame template={TemplateId} length={Length}")]
    public static partial void InboundFrame(this ILogger logger, int templateId, int length);

    // ---------------- Debug (2000–2999) ----------------

    [LoggerMessage(EventId = 2000, Level = LogLevel.Debug,
        Message = "FIXP state {From} -> {To} on {Trigger}")]
    public static partial void StateTransition(this ILogger logger, FixpClientState from, FixpClientState to, FixpClientTrigger trigger);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug,
        Message = "FIXP Negotiated with {Endpoint}")]
    public static partial void Negotiated(this ILogger logger, EndPoint endpoint);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Debug,
        Message = "FIXP Established with {Endpoint}")]
    public static partial void Established(this ILogger logger, EndPoint endpoint);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Debug,
        Message = "Retransmit request issued from={FromSeqNo} count={Count}")]
    public static partial void RetransmitRequestIssued(this ILogger logger, uint fromSeqNo, uint count);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Debug,
        Message = "Retransmit response received from={FromSeqNo} count={Count}")]
    public static partial void RetransmitResponseReceived(this ILogger logger, uint fromSeqNo, uint count);

    // ---------------- Information (3000–3999) ----------------

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information,
        Message = "EntryPointClient connected on attempt {Attempt}/{Max} to {Endpoint}")]
    public static partial void Connected(this ILogger logger, int attempt, int max, EndPoint endpoint);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information,
        Message = "Inbound Terminate received: code={Code}")]
    public static partial void InboundTerminate(this ILogger logger, int code);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information,
        Message = "TLS handshake completed with {Endpoint} (target host {TargetHost}, protocol {Protocol})")]
    public static partial void TlsHandshakeCompleted(this ILogger logger, EndPoint endpoint, string targetHost, string protocol);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Information,
        Message = "Persisted snapshot SessionID={Persisted} != current {Current}; ignoring.")]
    public static partial void StaleSnapshotIgnored(this ILogger logger, uint persisted, uint current);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Information,
        Message = "Recovered from snapshot: NextClientSeqNo={NextClientSeqNo} pending={PendingCount}")]
    public static partial void SnapshotRecovered(this ILogger logger, uint nextClientSeqNo, int pendingCount);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Information,
        Message = "DropCopy session up with {Endpoint}")]
    public static partial void DropCopyUp(this ILogger logger, EndPoint endpoint);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Information,
        Message = "Graceful Terminate sent to {Endpoint} (code={Code})")]
    public static partial void GracefulTerminate(this ILogger logger, EndPoint endpoint, int code);

    // ---------------- Warning (4000–4999) ----------------

    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning,
        Message = "ConnectAsync attempt {Attempt}/{Max} failed; retrying in {DelayMs} ms")]
    public static partial void ConnectRetry(this ILogger logger, Exception ex, int attempt, int max, int delayMs);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning,
        Message = "Idle timeout exceeded ({Idle} > {Threshold}); closing session")]
    public static partial void IdleTimeoutExceeded(this ILogger logger, TimeSpan idle, TimeSpan threshold);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning,
        Message = "Risk gate {Gate} {Kind}: {Reason}")]
    public static partial void RiskGateDecision(this ILogger logger, string gate, string kind, string? reason);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning,
        Message = "Failed to append OutboundDelta for ClOrdID={ClOrdID}")]
    public static partial void AppendDeltaFailed(this ILogger logger, Exception ex, string clOrdID);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning,
        Message = "Failed periodic snapshot compaction")]
    public static partial void SnapshotCompactionFailed(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Warning,
        Message = "Failed to persist OrderClosedDelta for ClOrdID={ClOrdID}")]
    public static partial void OrderClosedPersistFailed(this ILogger logger, Exception ex, string clOrdID);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning,
        Message = "NotApplied received fromSeqNo={FromSeqNo} count={Count}")]
    public static partial void NotAppliedReceived(this ILogger logger, uint fromSeqNo, uint count);

    [LoggerMessage(EventId = 4007, Level = LogLevel.Warning,
        Message = "BusinessReject received refSeqNo={RefSeqNo} reason={Reason}")]
    public static partial void BusinessRejectReceived(this ILogger logger, uint refSeqNo, int reason);

    [LoggerMessage(EventId = 4008, Level = LogLevel.Warning,
        Message = "Retransmit reject received reason={Reason}")]
    public static partial void RetransmitRejectReceived(this ILogger logger, int reason);

    // ---------------- Error (5000–5999) ----------------

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error,
        Message = "ConnectAsync exhausted {Max} retries to {Endpoint}")]
    public static partial void ConnectExhausted(this ILogger logger, Exception ex, int max, EndPoint endpoint);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Error,
        Message = "Inbound loop terminated by unhandled exception")]
    public static partial void InboundLoopFaulted(this ILogger logger, Exception ex);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Error,
        Message = "Session terminated due to fault")]
    public static partial void SessionFaulted(this ILogger logger, Exception ex);
}
