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
    /// <summary>
    /// Sequence number assigned by the gateway on the Recoverable outbound
    /// flow (FIXP session-protocol <c>Sequence</c> message family). Issue
    /// #228 Q3: events can arrive **out of numeric <see cref="SeqNum"/>
    /// order** — when a gap is detected, the post-gap frame is surfaced to
    /// consumers immediately (not buffered), and the lower-numbered
    /// gap-filling frames requested via <c>RetransmitRequest</c>
    /// (<c>Retransmission</c> reply, schema message ids 12/13) are
    /// delivered afterwards, once received. Do not implement dedup as a
    /// single "highest SeqNum seen" watermark that treats any lower value
    /// as an already-applied duplicate — that misclassifies the
    /// legitimately-new retransmitted frames that arrive after a
    /// higher-numbered frame as stale and silently drops them. Track
    /// applied events per-<see cref="SeqNum"/> (e.g. a seen-set) instead, or
    /// key idempotency off business identifiers (<c>ClOrdID</c>/<c>OrderId</c>)
    /// where duplicate delivery is otherwise a concern.
    /// Within a single reconnect-free session, gaps are always eventually
    /// filled this way; however, a gap still outstanding when the session
    /// terminates becomes permanently unrecoverable in-band — see
    /// <see cref="EntryPointClient.InboundGapAtReconnect"/>, which fires
    /// exactly once per reconnect in that case (the peer resets its
    /// outbound counter, so the missing range can never be retransmitted;
    /// consumers must reconcile out-of-band). Ordering is only meaningful
    /// at the session level (this counter), not per-order — do not assume,
    /// for a given order, that its own execution reports are contiguous in
    /// this sequence; other orders' events may interleave between them.
    /// </summary>
    public required ulong SeqNum { get; init; }

    /// <summary>Time the event was sent by the gateway.</summary>
    public required DateTimeOffset SendingTime { get; init; }
}

/// <summary>
/// Maps to <c>ExecutionReport_New</c> — order acknowledged. Issue #228:
/// the vendored schema (<c>b3-entrypoint-messages-8.4.2.xml</c>, message id
/// 200) does not define a <c>leavesQty</c> (tag 151) or <c>cumQty</c>
/// (tag 14) field on this message at all — B3 does not report a resting
/// quantity on the initial acknowledgment. <see cref="LeavesQty"/> and
/// <see cref="CumQty"/> are therefore ALWAYS <c>null</c> here, by design,
/// on every acceptance — this is not an intermittent/ambiguous condition.
/// Consumers must derive the initially-resting quantity from the
/// submitted order's own <c>OrderQty</c> (tracked client-side), never from
/// this event. In particular, do not coalesce a null here to <c>0</c>
/// (e.g. <c>LeavesQty ?? 0UL</c>) and treat the result as "no quantity
/// remaining" — that misreads "field absent on this message type" as
/// "order fully closed" and will incorrectly mark a just-accepted,
/// fully-resting order as closed.
/// </summary>
public sealed record OrderAccepted : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ulong OrderId { get; init; }
    public required OrderStatus OrderStatus { get; init; }
    public required ulong SecurityId { get; init; }
    public required Side Side { get; init; }
    /// <summary>
    /// Always <c>null</c> — see the type-level remarks. <c>ExecutionReport_New</c>
    /// has no wire representation for this field.
    /// </summary>
    public ulong? LeavesQty { get; init; }
    /// <summary>
    /// Always <c>null</c> — see the type-level remarks. <c>ExecutionReport_New</c>
    /// has no wire representation for this field.
    /// </summary>
    public ulong? CumQty { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>
/// Maps to <c>ExecutionReport_Modify</c>. Issue #228: unlike
/// <see cref="OrderAccepted"/>, <see cref="LeavesQty"/> and
/// <see cref="CumQty"/> map to schema fields (tags 151/14) that are
/// <c>required</c> (type <c>Quantity</c>, not <c>QuantityOptional</c>,
/// no <c>presence="optional"</c>, no null-value sentinel) — the venue
/// always sends a real value for both on every <c>ExecutionReport_Modify</c>,
/// including unsolicited restatements (e.g. iceberg replenishment). These
/// properties are typed as nullable only for structural symmetry across
/// the <see cref="EntryPointEvent"/> discriminated union; in practice they
/// are never actually <c>null</c> for this event type, so a
/// <c>?? 0UL</c> fallback is safe here (an always-dead branch) but should
/// not be copy-pasted onto <see cref="OrderAccepted"/>, where the same
/// fallback silently misreports "field never sent" as "fully filled".
/// </summary>
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

/// <summary>
/// Maps to <c>ExecutionReport_Cancel</c>. Issue #228: this message has no
/// <c>leavesQty</c> field on the wire (a cancel is a terminal state with
/// 0 remaining quantity by definition — there is nothing to reconcile),
/// which is why this record exposes no <c>LeavesQty</c> property. Note it
/// does declare a required <c>cumQty</c> (tag 14) on the wire, same as
/// <c>ExecutionReport_Modify</c>/<c>_Trade</c> — this record simply
/// doesn't surface it (not currently needed by any consumer); it is not
/// absent from the wire the way <c>leavesQty</c> is.
/// <c>ExecutionReport_Cancel</c> is also the message used for unsolicited
/// administrative cancellations (Market Operations, Cancel On Disconnect,
/// self-trade prevention, etc. — see <see cref="RestatementReason"/> /
/// schema enum <c>ExecRestatementReason</c>), not only solicited
/// cancel-request acks.
/// </summary>
public sealed record OrderCancelled : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ClOrdID? OrigClOrdID { get; init; }
    public required ulong OrderId { get; init; }
    public required OrderStatus OrderStatus { get; init; }
    public ExecRestatementReason? RestatementReason { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>
/// Maps to <c>ExecutionReport_Trade</c>. Issue #228: like
/// <see cref="OrderModified"/> (and unlike <see cref="OrderAccepted"/>),
/// <see cref="LeavesQty"/>/<see cref="CumQty"/> map to schema fields that
/// are <c>required</c> on the wire (type <c>Quantity</c>, no null-value
/// sentinel) — always populated with the real post-trade remaining/filled
/// quantity, never <c>null</c> in practice for this event type.
/// </summary>
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

/// <summary>Maps to <c>OrderMassActionReport</c> (template 702). Confirmation /
/// rejection event for a previously sent <see cref="MassActionRequest"/>; also
/// emitted on Drop Copy sessions for any mass action affecting the firm.</summary>
public sealed record MassActionExecuted : EntryPointEvent
{
    public required ClOrdID ClOrdID { get; init; }
    public required ulong MassActionReportId { get; init; }
    public required MassActionType ActionType { get; init; }
    public required MassActionScope Scope { get; init; }
    public required MassActionResponse Response { get; init; }
    public MassActionRejectReason? RejectReason { get; init; }
    public ExecRestatementReason? RestatementReason { get; init; }
    public Side? Side { get; init; }
    public ulong? SecurityId { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Allocation transaction type (schema enum <c>AllocTransType</c>).</summary>
public enum AllocTransType : byte
{
    New = (byte)'0',
    Cancel = (byte)'2',
}

/// <summary>Allocation report purpose (schema enum <c>AllocReportType</c>).</summary>
public enum AllocReportType : byte
{
    RequestToIntermediary = (byte)'8',
}

/// <summary>How orders are booked / allocated (schema enum <c>AllocNoOrdersType</c>).</summary>
public enum AllocNoOrdersType : byte
{
    NotSpecified = (byte)'0',
}

/// <summary>Allocation lifecycle status (schema enum <c>AllocStatus</c>).</summary>
public enum AllocStatus : byte
{
    Accepted = (byte)'0',
    RejectedByIntermediary = (byte)'5',
}

/// <summary>Position transaction type (schema enum <c>PosTransType</c>).</summary>
public enum PosTransType : byte
{
    Exercise = 1,
    AutomaticExercise = 105,
    ExerciseNotAutomatic = 106,
}

/// <summary>Position maintenance action (schema enum <c>PosMaintAction</c>).</summary>
public enum PosMaintAction : byte
{
    New = (byte)'1',
    Cancel = (byte)'3',
}

/// <summary>Position maintenance status (schema enum <c>PosMaintStatus</c>).</summary>
public enum PosMaintStatus : byte
{
    Accepted = (byte)'0',
    Rejected = (byte)'2',
    Completed = (byte)'3',
    NotExecuted = (byte)'9',
}

/// <summary>Maps to <c>AllocationReport</c> (template 602). Post-trade
/// allocation lifecycle event; surfaces on both Order Entry and Drop Copy
/// sessions when an allocation is booked, accepted or rejected.</summary>
public sealed record AllocationReceived : EntryPointEvent
{
    public required ulong AllocId { get; init; }
    public required ulong AllocReportId { get; init; }
    public required ulong SecurityId { get; init; }
    public required AllocTransType TransType { get; init; }
    public required AllocReportType ReportType { get; init; }
    public required AllocStatus Status { get; init; }
    public required ulong Quantity { get; init; }
    public required Side Side { get; init; }
    public AllocNoOrdersType? NoOrdersType { get; init; }
    public uint? RejCode { get; init; }
    public ushort? TradeDate { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}

/// <summary>Maps to <c>PositionMaintenanceReport</c> (template 503). Confirmation
/// or rejection of a previously sent PositionMaintenanceRequest (or its cancel
/// variant). Surfaces on Drop Copy sessions for any PMR affecting the firm.</summary>
public sealed record PositionMaintenanceReceived : EntryPointEvent
{
    public required ulong PosMaintRptId { get; init; }
    public required ulong SecurityId { get; init; }
    public required PosTransType TransType { get; init; }
    public required PosMaintAction Action { get; init; }
    public required PosMaintStatus Status { get; init; }
    public ulong? PosReqId { get; init; }
    public uint? TradeId { get; init; }
    public ulong? OrigPosReqRefId { get; init; }
    public AccountType? AccountType { get; init; }
    public uint? Account { get; init; }
    public ushort? ClearingBusinessDate { get; init; }
    public uint? PosMaintResult { get; init; }
    public DateTimeOffset? TransactTime { get; init; }
}
