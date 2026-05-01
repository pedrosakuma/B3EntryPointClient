namespace B3.EntryPoint.Client.Models;

public enum Side : byte { Buy = 1, Sell = 2 }

public enum OrderType : byte { Market = 1, Limit = 2, Stop = 3, StopLimit = 4 }

public enum TimeInForce : byte { Day = 0, GoodTillCancel = 1, ImmediateOrCancel = 3, FillOrKill = 4 }

/// <summary>
/// Logical order submission request. The library converts user-friendly fields
/// (e.g. decimal <see cref="Price"/>) to wire encodings (price /10000 mantissa).
/// Wire-level encoding lands with the order-entry milestone; bootstrap exposes
/// the shape only.
/// </summary>
public sealed record SubmitOrderRequest
{
    public required string ClOrdID { get; init; }
    public required uint SecurityId { get; init; }
    public required Side Side { get; init; }
    public required OrderType Type { get; init; }
    public decimal? Price { get; init; }
    public required ulong Quantity { get; init; }
    public TimeInForce Tif { get; init; } = TimeInForce.Day;
}
