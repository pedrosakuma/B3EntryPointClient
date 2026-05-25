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
