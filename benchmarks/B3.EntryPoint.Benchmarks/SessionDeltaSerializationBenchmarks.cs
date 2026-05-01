using System.Text.Json;
using BenchmarkDotNet.Attributes;
using B3.EntryPoint.Client.State;

namespace B3.EntryPoint.Benchmarks;

/// <summary>
/// Cost of serializing the polymorphic <see cref="SessionDelta"/> wire envelope.
/// AppendDeltaAsync calls this on every order entry frame, so allocations
/// here directly bloat the GC pause budget.
/// </summary>
[MemoryDiagnoser]
public class SessionDeltaSerializationBenchmarks
{
    private SessionDelta _outbound = null!;
    private SessionDelta _inbound = null!;
    private SessionSnapshot _snapshot = null!;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    [GlobalSetup]
    public void Setup()
    {
        _outbound = new OutboundDelta(SeqNum: 12345UL, ClOrdID: "ACME0000001", SecurityId: 5_900_000UL);
        _inbound = new InboundDelta(SeqNum: 67890UL);
        _snapshot = new SessionSnapshot
        {
            SessionId = 42u,
            SessionVerId = 1u,
            LastOutboundSeqNum = 100_000UL,
            LastInboundSeqNum = 99_000UL,
            CapturedAt = DateTimeOffset.UtcNow,
            OutstandingOrders = Enumerable.Range(0, 100)
                .ToDictionary(i => $"ACME{i:D7}", i => 5_900_000UL + (ulong)i),
        };
    }

    [Benchmark] public string OutboundDelta() => JsonSerializer.Serialize<SessionDelta>(_outbound, Options);
    [Benchmark] public string InboundDelta() => JsonSerializer.Serialize<SessionDelta>(_inbound, Options);
    [Benchmark] public string Snapshot100Orders() => JsonSerializer.Serialize(_snapshot, Options);
}
