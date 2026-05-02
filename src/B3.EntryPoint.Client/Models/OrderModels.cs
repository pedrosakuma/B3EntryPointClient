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
    public AccountType AccountType { get; init; } = AccountType.RegularAccount;
    public ulong? Account { get; init; }
    public DateTimeOffset? ExpireDate { get; init; }
    public ulong? MinQty { get; init; }
    public ulong? MaxFloor { get; init; }
    public string? MemoText { get; init; }
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

