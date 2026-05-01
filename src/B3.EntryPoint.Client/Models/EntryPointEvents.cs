namespace B3.EntryPoint.Client.Models;

/// <summary>Order status (schema enum <c>OrdStatus</c>).</summary>
public enum OrderStatus : byte
{
    New = (byte)'0',
    PartiallyFilled = (byte)'1',
    Filled = (byte)'2',
    Cancelled = (byte)'4',
    Replaced = (byte)'5',
    Rejected = (byte)'8',
    Expired = (byte)'C',
    Restated = (byte)'R',
    PreviousFinalState = (byte)'Z',
}

/// <summary>Reason for an Execution Report restatement (schema enum <c>ExecRestatementReason</c>).</summary>
public enum ExecRestatementReason : byte
{
    GtRestatement = 1,
    MarketOption = 8,
    CancelOnHardDisconnection = 100,
    CancelOnTerminate = 101,
    CancelOnDisconnectAndTerminate = 102,
    SelfTradingPrevention = 103,
    CancelFromFirmsoft = 105,
    CancelRestingOrderOnSelfTrade = 107,
    MarketMakerProtection = 200,
    RiskManagementCancellation = 201,
    OrderMassActionFromClientRequest = 202,
    CancelOrderDueToOperationalError = 203,
    OrderCancelledDueToOperationalError = 204,
    CancelOrderFirmsoftDueToOperationalError = 205,
    OrderCancelledFirmsoftDueToOperationalError = 206,
    MassCancelOrderDueToOperationalErrorRequest = 207,
    MassCancelOrderDueToOperationalErrorEffective = 208,
    CancelMinimumQtyBlock = 209,
    CancelRemainingFromSweepCross = 210,
    MassCancelOnBehalf = 211,
    MassCancelOnBehalfDueToOperationalErrorEffective = 212,
    CancelOnMidpointBrokerOnlyRemoval = 213,
}

/// <summary>
/// Base discriminated record for unsolicited events surfaced by
/// <see cref="EntryPointClient.Events"/>. Subtypes map 1-to-1 to the
/// <c>ExecutionReport_*</c> family plus <c>BusinessMessageReject</c>
/// (schema §6).
/// </summary>
public abstract record EntryPointEvent
{
    /// <summary>Sequence number assigned by the gateway.</summary>
    public required ulong SeqNum { get; init; }

    /// <summary>Time the event was sent by the gateway.</summary>
    public required DateTimeOffset SendingTime { get; init; }
}

/// <summary>Maps to <c>ExecutionReport_New</c> — order acknowledged.</summary>
public sealed record OrderAccepted : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ulong OrderId { get; init; }
    public required OrderStatus OrderStatus { get; init; }
    public required ulong SecurityId { get; init; }
    public required Side Side { get; init; }
    public ulong? LeavesQty { get; init; }
    public ulong? CumQty { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Maps to <c>ExecutionReport_Modify</c>.</summary>
public sealed record OrderModified : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ClOrdID OrigClOrdID { get; init; }
    public required ulong OrderId { get; init; }
    public required OrderStatus OrderStatus { get; init; }
    public ulong? LeavesQty { get; init; }
    public ulong? CumQty { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Maps to <c>ExecutionReport_Cancel</c>.</summary>
public sealed record OrderCancelled : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ClOrdID? OrigClOrdID { get; init; }
    public required ulong OrderId { get; init; }
    public required OrderStatus OrderStatus { get; init; }
    public ExecRestatementReason? RestatementReason { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Maps to <c>ExecutionReport_Trade</c>.</summary>
public sealed record OrderTrade : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ulong OrderId { get; init; }
    public required ulong TradeId { get; init; }
    public required OrderStatus OrderStatus { get; init; }
    public required decimal LastPx { get; init; }
    public required ulong LastQty { get; init; }
    public ulong? LeavesQty { get; init; }
    public ulong? CumQty { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Maps to <c>ExecutionReport_Reject</c>.</summary>
public sealed record OrderRejected : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ulong OrderId { get; init; }
    public required ushort RejectCode { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Maps to <c>ExecutionReport_Forward</c> — order forwarded to another venue/segment.</summary>
public sealed record OrderForwarded : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ulong OrderId { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Maps to <c>BusinessMessageReject</c>.</summary>
public sealed record BusinessReject : EntryPointEvent
{
    public required ulong RefSeqNum { get; init; }
    public required ushort RejectReason { get; init; }
    public string? Text { get; init; }
}

/// <summary>Maps to <c>QuoteRequestReject</c> (template 405). The exchange rejected
/// a previously submitted <see cref="QuoteRequestMessage"/>.</summary>
public sealed record QuoteRequestRejected : EntryPointEvent
{
    public required string QuoteReqId { get; init; }
    public required ulong SecurityId { get; init; }
    public string? QuoteId { get; init; }
    public uint? RejectReason { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Maps to <c>QuoteStatusReport</c> (template 402). Carries the
/// lifecycle status of a quote previously submitted via <see cref="QuoteMessage"/>.</summary>
public sealed record QuoteStatusUpdated : EntryPointEvent
{
    public required string QuoteId { get; init; }
    public required string QuoteReqId { get; init; }
    public required ulong SecurityId { get; init; }
    public required QuoteStatus Status { get; init; }
    public uint? QuoteRejectReason { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}
