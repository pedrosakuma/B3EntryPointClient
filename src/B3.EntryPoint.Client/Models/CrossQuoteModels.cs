namespace B3.EntryPoint.Client.Models;

/// <summary>Cross trade type for <c>NewOrderCross</c> (FIX <c>CrossType</c>, schema enum).</summary>
public enum CrossType : byte
{
    AllOrNone = 1,
    CrossExecutedAgainstBookFromClient = 4,
    VwapCross = 7,
    ClosingPriceCross = 8,
}

/// <summary>Cross prioritization (FIX <c>CrossPrioritization</c>).</summary>
public enum CrossPrioritization : byte
{
    None = 0,
    BuySidePrioritized = 1,
    SellSidePrioritized = 2,
}

/// <summary>Side of a sub-order leg inside a cross.</summary>
public sealed record CrossLeg
{
    public required ClOrdID ClOrdID { get; init; }
    public required Side Side { get; init; }
    public required ulong OrderQty { get; init; }
    public AccountType AccountType { get; init; } = AccountType.RegularAccount;
    public ulong? Account { get; init; }
}

/// <summary>Logical request shape for <c>NewOrderCross</c> (schema §9).</summary>
public sealed record NewOrderCrossRequest
{
    public required string CrossId { get; init; }
    public required ulong SecurityId { get; init; }
    public required CrossType CrossType { get; init; }
    public required CrossPrioritization Prioritization { get; init; }
    public required decimal Price { get; init; }
    public required IReadOnlyList<CrossLeg> Legs { get; init; }
}

/// <summary>Indicates who in the contract has control over evoking settlement
/// (B3 <c>SettlType</c>: BuyersDiscretion=<c>'0'</c>, SellersDiscretion=<c>'8'</c>, Mutual=<c>'X'</c>).</summary>
public enum SettlementType : byte
{
    BuyersDiscretion = (byte)'0',
    SellersDiscretion = (byte)'8',
    Mutual = (byte)'X',
}

/// <summary>Indicates a simultaneous trade of the underlying when reporting Termo Vista forwards.</summary>
public enum ExecuteUnderlyingTrade : byte
{
    NoUnderlyingTrade = (byte)'0',
    UnderlyingOpposingTrade = (byte)'1',
}

/// <summary>
/// Logical request shape for <c>QuoteRequest</c> (B3 Termo §10).
/// Models a forward (Termo) quote request — required fields mirror the wire
/// payload; <see cref="QuoteReqId"/> is exposed as a string for FIX semantics
/// and parsed to <c>ulong</c> on encode.
/// </summary>
public sealed record QuoteRequestMessage
{
    public required string QuoteReqId { get; init; }
    public required ulong SecurityId { get; init; }
    public required Side Side { get; init; }
    public required decimal Price { get; init; }
    public required ulong OrderQty { get; init; }
    public required SettlementType SettlType { get; init; }
    public required ushort DaysToSettlement { get; init; }
    public required uint ContraBroker { get; init; }
    public decimal FixedRate { get; init; }
    public string? QuoteId { get; init; }
    public uint? TradeId { get; init; }
    public ExecuteUnderlyingTrade? ExecuteUnderlyingTrade { get; init; }
}

/// <summary>
/// Logical request shape for <c>Quote</c> (B3 Termo §10).
/// Models the response a Termo dealer sends back to a <see cref="QuoteRequestMessage"/>.
/// One side per message — emit two messages to quote both bid and offer.
/// </summary>
public sealed record QuoteMessage
{
    public required string QuoteId { get; init; }
    public required ulong SecurityId { get; init; }
    public required Side Side { get; init; }
    public required ulong OrderQty { get; init; }
    public required SettlementType SettlType { get; init; }
    public required ushort DaysToSettlement { get; init; }
    public decimal? Price { get; init; }
    public decimal FixedRate { get; init; }
    public string? QuoteReqId { get; init; }
    public uint? Account { get; init; }
    public uint? TradingSubAccount { get; init; }
    public ExecuteUnderlyingTrade? ExecuteUnderlyingTrade { get; init; }
}

/// <summary>Reason carried by <c>QuoteRequestReject</c>. The wire field is a free-form
/// <c>uint</c>; common FIX values are surfaced for convenience but the underlying
/// integer is preserved on the inbound event.</summary>
public enum QuoteRequestRejectReason : uint
{
    UnknownSymbol = 1,
    ExchangeClosed = 2,
    QuoteRequestExceedsLimit = 3,
    TooLateToEnter = 4,
    InvalidPrice = 5,
    NotAuthorizedToRequestQuote = 6,
    Other = 99,
}

/// <summary>Status of an outstanding quote (matches B3 <c>QuoteStatus</c> v8.4.2).</summary>
public enum QuoteStatus : byte
{
    Accepted = 0,
    Rejected = 5,
    Expired = 7,
    QuoteNotFound = 9,
    Pending = 10,
    Pass = 11,
    Canceled = 17,
}
