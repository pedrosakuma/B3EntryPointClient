namespace B3.EntryPoint.Client.Risk;

/// <summary>Outcome of a pre-trade risk evaluation.</summary>
public enum RiskDecisionKind : byte
{
    Allow = 0,
    Reject = 1,
    Throttle = 2,
}

/// <summary>Result returned by an <see cref="IPreTradeGate"/>.</summary>
public readonly struct RiskDecision
{
    public RiskDecisionKind Kind { get; init; }
    public string? Reason { get; init; }

    public static RiskDecision Allow() => new() { Kind = RiskDecisionKind.Allow };
    public static RiskDecision Reject(string reason) => new() { Kind = RiskDecisionKind.Reject, Reason = reason };
    public static RiskDecision Throttle(string reason) => new() { Kind = RiskDecisionKind.Throttle, Reason = reason };
}

public enum OutboundRequestKind : byte
{
    NewOrder,
    SimpleNewOrder,
    Replace,
    SimpleReplace,
    Cancel,
    MassAction,
}

/// <summary>
/// Snapshot of an outbound request fed to <see cref="IPreTradeGate"/>. Variants
/// other than NewOrder are normalized through the same shape so a single gate
/// can apply uniform throttles across kinds.
/// </summary>
public readonly record struct OutboundRequest(
    OutboundRequestKind Kind,
    object Request,
    ulong SecurityId,
    string ClOrdID);

/// <summary>
/// Pre-trade risk gate. Implementations are invoked sequentially before any
/// order entry frame leaves the client. The first non-Allow decision aborts
/// the request and surfaces a <see cref="RiskRejectedException"/>.
/// </summary>
public interface IPreTradeGate
{
    ValueTask<RiskDecision> EvaluateAsync(OutboundRequest request, CancellationToken ct);
}

/// <summary>Thrown when a pre-trade gate rejects or throttles an outbound request.</summary>
public sealed class RiskRejectedException : Exception
{
    public RiskDecisionKind Kind { get; }
    public RiskRejectedException(RiskDecision decision)
        : base($"Pre-trade gate {decision.Kind}: {decision.Reason ?? "(no reason)"}")
    {
        Kind = decision.Kind;
    }
}
