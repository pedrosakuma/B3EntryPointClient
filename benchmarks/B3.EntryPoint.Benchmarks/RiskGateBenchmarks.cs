using BenchmarkDotNet.Attributes;
using B3.EntryPoint.Client.Risk;

namespace B3.EntryPoint.Benchmarks;

/// <summary>
/// Hot-path latency for the pre-trade risk gate. Every order entry call
/// pays this cost, so regressions here directly hit end-to-end p99.
/// </summary>
[MemoryDiagnoser]
public class RiskGateBenchmarks
{
    private ClOrdIdPrefixThrottle _prefix = null!;
    private SecurityIdRateThrottle _security = null!;
    private OutboundRequest _request;

    [GlobalSetup]
    public void Setup()
    {
        _prefix = new ClOrdIdPrefixThrottle(prefixLength: 4, maxPerWindow: 1_000_000, windowDuration: TimeSpan.FromSeconds(1));
        _security = new SecurityIdRateThrottle(maxPerWindow: 1_000_000, windowDuration: TimeSpan.FromSeconds(1));
        _request = new OutboundRequest(OutboundRequestKind.NewOrder, new object(), SecurityId: 5_900_000UL, ClOrdID: "ACME0000001");
    }

    [Benchmark]
    public async ValueTask<RiskDecision> ClOrdIdPrefix() => await _prefix.EvaluateAsync(_request, CancellationToken.None);

    [Benchmark]
    public async ValueTask<RiskDecision> SecurityIdRate() => await _security.EvaluateAsync(_request, CancellationToken.None);
}
