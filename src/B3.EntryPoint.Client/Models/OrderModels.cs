namespace B3.EntryPoint.Client.Models;

/// <summary>Side of order (FIX <c>Side</c>, schema enum <c>Side</c>).</summary>
public enum Side : byte
{
    Buy = (byte)'1',
    Sell = (byte)'2',
}

/// <summary>
/// Order type for <c>NewOrderSingle</c> (schema enum <c>OrdType</c>).
/// </summary>
public enum OrderType : byte
{
    Market = (byte)'1',
    Limit = (byte)'2',
    StopLoss = (byte)'3',
    StopLimit = (byte)'4',
    MarketWithLeftoverAsLimit = (byte)'K',
    Rlp = (byte)'W',
    PeggedMidpoint = (byte)'P',
}

/// <summary>Simplified order type for <c>SimpleNewOrder</c> (schema enum <c>SimpleOrdType</c>).</summary>
public enum SimpleOrderType : byte
{
    Market = (byte)'1',
    Limit = (byte)'2',
}

/// <summary>Time in force (schema enum <c>TimeInForce</c>).</summary>
public enum TimeInForce : byte
{
    Day = (byte)'0',
    GoodTillCancel = (byte)'1',
    ImmediateOrCancel = (byte)'3',
    FillOrKill = (byte)'4',
    GoodTillDate = (byte)'6',
    AtTheClose = (byte)'7',
    GoodForAuction = (byte)'A',
}

/// <summary>Time in force for <c>SimpleNewOrder</c> (schema enum <c>SimpleTimeInForce</c>).</summary>
public enum SimpleTimeInForce : byte
{
    Day = (byte)'0',
    ImmediateOrCancel = (byte)'3',
    FillOrKill = (byte)'4',
}

/// <summary>Account type (schema enum <c>AccountType</c>).</summary>
public enum AccountType : byte
{
    RemoveAccountInformation = 38,
    RegularAccount = 39,
}

/// <summary>
/// Self-trade prevention instruction (schema enum
/// <c>SelfTradePreventionInstruction</c>, FIX tag 35539). Indicates which
/// order should be canceled when a self-trade is detected.
/// </summary>
public enum SelfTradePreventionInstruction : byte
{
    None = 0,
    CancelAggressorOrder = 1,
    CancelRestingOrder = 2,
    CancelBothOrders = 3,
}

/// <summary>
/// Additional order routing instruction (schema enum <c>RoutingInstruction</c>,
/// FIX tag 35487, optional, <c>sinceVersion=2</c>).
/// </summary>
public enum RoutingInstruction : byte
{
    RetailLiquidityTaker = 1,
    WaivedPriority = 2,
    BrokerOnly = 3,
    BrokerOnlyRemoval = 4,
}

/// <summary>
/// Self-trade prevention / mass-cancel-on-behalf investor identification
/// (schema composite <c>InvestorID</c>, FIX tag 35508).
/// </summary>
public readonly record struct InvestorId(ushort Prefix, uint Document);

/// <summary>
/// Logical request shape for <c>NewOrderSingle</c> (schema §6). The client
/// converts user-friendly fields (decimal <see cref="Price"/>) to the wire
/// encoding (fixed-point mantissa with exponent -4, i.e. <c>price × 10_000</c>)
/// when serialising the SBE message.
/// </summary>
public sealed record NewOrderRequest
{
    public required ClOrdID ClOrdID { get; init; }
    public required ulong SecurityId { get; init; }
    public required Side Side { get; init; }
    public required OrderType OrderType { get; init; }
    public decimal? Price { get; init; }
    public decimal? StopPrice { get; init; }
    public required ulong OrderQty { get; init; }
    public TimeInForce TimeInForce { get; init; } = TimeInForce.Day;
    public ulong? Account { get; init; }
    public DateTimeOffset? ExpireDate { get; init; }
    public ulong? MinQty { get; init; }
    public ulong? MaxFloor { get; init; }
    public string? MemoText { get; init; }
    /// <summary>FIX tag 35505 — order tag identifier. Optional; default = absent (wire null = 0).</summary>
    public byte? OrdTagId { get; init; }
    /// <summary>FIX tag 9773 — when <see langword="true"/>, resets Market Maker Protection.</summary>
    public bool MmProtectionReset { get; init; }
    /// <summary>FIX tag 35539 — self-trade prevention instruction. Defaults to <see cref="SelfTradePreventionInstruction.None"/>.</summary>
    public SelfTradePreventionInstruction SelfTradePreventionInstruction { get; init; } = SelfTradePreventionInstruction.None;
    /// <summary>FIX tag 35487 — additional routing instruction (optional, <c>sinceVersion=2</c>).</summary>
    public RoutingInstruction? RoutingInstruction { get; init; }
    /// <summary>FIX tag 35508 — investor id for self-trade prevention / mass-cancel-on-behalf (<c>sinceVersion=1</c>).</summary>
    public InvestorId? InvestorId { get; init; }
    /// <summary>FIX tag 35121 — trading sub-account for associating risk limits (optional, <c>sinceVersion=5</c>).</summary>
    public uint? TradingSubAccount { get; init; }
}

/// <summary>
/// Logical request shape for <c>SimpleNewOrder</c> — the lightweight order
/// entry path with a reduced field set (schema §6).
/// </summary>
public sealed record SimpleNewOrderRequest
{
    public required ClOrdID ClOrdID { get; init; }
    public required ulong SecurityId { get; init; }
    public required Side Side { get; init; }
    public required SimpleOrderType OrderType { get; init; }
    public decimal? Price { get; init; }
    public required ulong OrderQty { get; init; }
    public SimpleTimeInForce TimeInForce { get; init; } = SimpleTimeInForce.Day;
    public ulong? Account { get; init; }
}

