namespace B3.EntryPoint.Client.Models;

/// <summary>Type of mass action requested (schema enum <c>MassActionType</c>).</summary>
public enum MassActionType : byte
{
    ReleaseOrdersFromSuspension = 2,
    CancelOrders = 3,
    CancelAndSuspendOrders = 4,
}

/// <summary>Scope of mass action (schema enum <c>MassActionScope</c>).</summary>
public enum MassActionScope : byte
{
    AllOrdersForATradingSession = 6,
}

/// <summary>Mass action response (schema enum <c>MassActionResponse</c>).</summary>
public enum MassActionResponse : byte
{
    Rejected = (byte)'0',
    Accepted = (byte)'1',
}

/// <summary>Reason a mass action was rejected (schema enum <c>MassActionRejectReason</c>).</summary>
public enum MassActionRejectReason : byte
{
    MassActionNotSupported = 0,
    InvalidOrUnknownMarketSegment = 8,
    Other = 99,
}

/// <summary>
/// Reason supplied with a solicited <c>OrderCancelRequest</c> (schema enum
/// <c>ExecRestatementReasonValidForSingleCancel</c>, FIX tag 378).
/// </summary>
public enum CancelExecRestatementReason : byte
{
    /// <summary>Cancel was sent because of an operational error.</summary>
    CancelOrderDueToOperationalError = 203,
}

/// <summary>
/// Logical request shape for <c>OrderCancelRequest</c> (schema §6).
/// </summary>
public sealed record CancelOrderRequest
{
    public required ClOrdID ClOrdID { get; init; }
    public required ClOrdID OrigClOrdID { get; init; }
    public required ulong SecurityId { get; init; }
    public required Side Side { get; init; }
    public ulong? Account { get; init; }
    /// <summary>Venue-assigned order id (FIX tag 37). Alternative to <see cref="OrigClOrdID"/> for cancelling by venue id.</summary>
    public ulong? OrderId { get; init; }
    /// <summary>Reason supplied with a solicited cancel (FIX tag 378). When null no reason is sent.</summary>
    public CancelExecRestatementReason? ExecRestatementReason { get; init; }
    /// <summary>5-char executing trader id (FIX tag 35506). When null the field is left as the wire null sentinel.</summary>
    public string? ExecutingTrader { get; init; }
    /// <summary>Trading desk id (FIX tag 35510, varData). Encoded as ASCII; max 255 bytes.</summary>
    public string? DeskId { get; init; }
    public string? MemoText { get; init; }
}

/// <summary>
/// Logical request shape for <c>OrderMassActionRequest</c> (schema §6).
/// </summary>
public sealed record MassActionRequest
{
    public required ClOrdID ClOrdID { get; init; }
    public required MassActionType ActionType { get; init; }
    public MassActionScope Scope { get; init; } = MassActionScope.AllOrdersForATradingSession;
    public ulong? SecurityId { get; init; }
    public Side? Side { get; init; }
    public ulong? Account { get; init; }
    public string? MarketSegment { get; init; }
}

/// <summary>
/// Logical response shape for <c>OrderMassActionReport</c> (schema §6).
/// </summary>
public sealed record MassActionReport
{
    public required ClOrdID ClOrdID { get; init; }
    public required MassActionResponse Response { get; init; }
    public required MassActionType ActionType { get; init; }
    public required MassActionScope Scope { get; init; }
    public uint? TotalAffectedOrders { get; init; }
    public MassActionRejectReason? RejectReason { get; init; }
    public string? Reason { get; init; }
}
