namespace B3.EntryPoint.Client.Models;

/// <summary>
/// Logical request shape for <c>OrderCancelReplaceRequest</c> (schema §6).
/// Carries the new ClOrdID assigned to the replacement plus the original
/// ClOrdID being modified.
/// </summary>
public sealed record ReplaceOrderRequest
{
    public required ClOrdID ClOrdID { get; init; }
    public required ClOrdID OrigClOrdID { get; init; }
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
/// Logical request shape for <c>SimpleModifyOrder</c> — minimal modification
/// path matching <c>SimpleNewOrder</c> (schema §6).
/// </summary>
public sealed record SimpleModifyRequest
{
    public required ClOrdID ClOrdID { get; init; }
    public required ClOrdID OrigClOrdID { get; init; }
    public required ulong SecurityId { get; init; }
    public required Side Side { get; init; }
    public required SimpleOrderType OrderType { get; init; }
    public decimal? Price { get; init; }
    public required ulong OrderQty { get; init; }
    public SimpleTimeInForce TimeInForce { get; init; } = SimpleTimeInForce.Day;
}
