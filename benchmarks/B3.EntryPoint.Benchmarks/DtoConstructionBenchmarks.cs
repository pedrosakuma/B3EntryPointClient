using BenchmarkDotNet.Attributes;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Benchmarks;

/// <summary>
/// Allocation/throughput baseline for constructing the public DTOs the
/// matching platform will hand to the client. Tracks regressions in record
/// initialization, default values, and ClOrdID parsing.
/// </summary>
[MemoryDiagnoser]
public class DtoConstructionBenchmarks
{
    [Benchmark]
    public NewOrderRequest NewOrderSingle()
        => new()
        {
            ClOrdID = new ClOrdID(123456789UL),
            SecurityId = 5_900_000UL,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = 42.55m,
            OrderQty = 100UL,
            TimeInForce = TimeInForce.Day,
        };

    [Benchmark]
    public SimpleNewOrderRequest SimpleNewOrder()
        => new()
        {
            ClOrdID = new ClOrdID(987654321UL),
            SecurityId = 5_900_000UL,
            Side = Side.Sell,
            OrderType = SimpleOrderType.Limit,
            Price = 42.55m,
            OrderQty = 100UL,
            TimeInForce = SimpleTimeInForce.Day,
        };

    [Benchmark]
    public ClOrdID ClOrdIdParse() => ClOrdID.Parse("1234567890");
}
