namespace B3.EntryPoint.Client.Models;

/// <summary>Cross trade type for <c>NewOrderCross</c> (FIX <c>CrossType</c>).</summary>
public enum CrossType : byte
{
    AllOrNone = 1,
    Default = 2,
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

/// <summary>Logical request shape for <c>QuoteRequest</c>.</summary>
public sealed record QuoteRequestMessage
{
    public required string QuoteReqId { get; init; }
    public required ulong SecurityId { get; init; }
    public ulong? OrderQty { get; init; }
    public Side? Side { get; init; }
}

/// <summary>Logical request shape for <c>Quote</c> (a market-maker quote).</summary>
public sealed record QuoteMessage
{
    public required string QuoteId { get; init; }
    public required ulong SecurityId { get; init; }
    public decimal? BidPrice { get; init; }
    public decimal? OfferPrice { get; init; }
    public ulong? BidSize { get; init; }
    public ulong? OfferSize { get; init; }
    public string? QuoteReqId { get; init; }
}

/// <summary>Reason carried by <c>QuoteRequestReject</c> (FIX <c>QuoteRequestRejectReason</c>).</summary>
public enum QuoteRequestRejectReason : byte
{
    UnknownSymbol = 1,
    ExchangeClosed = 2,
    QuoteRequestExceedsLimit = 3,
    TooLateToEnter = 4,
    InvalidPrice = 5,
    NotAuthorizedToRequestQuote = 6,
    Other = 99,
}

/// <summary>Status of an outstanding quote (FIX <c>QuoteStatus</c> subset).</summary>
public enum QuoteStatus : byte
{
    Accepted = 0,
    CanceledForSymbol = 1,
    CanceledAll = 4,
    Rejected = 5,
    RemovedFromMarket = 6,
    Expired = 7,
    Active = 16,
}
