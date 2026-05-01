using B3.EntryPoint.Client.Risk;

namespace B3.EntryPoint.Client.Tests.Risk;

public class ThrottleTests
{
    [Fact]
    public async Task ClOrdIdPrefixThrottle_AllowsUpToLimit_ThenThrottles()
    {
        var t = new ClOrdIdPrefixThrottle(prefixLength: 3, maxPerWindow: 2, windowDuration: TimeSpan.FromSeconds(10));
        var req = new OutboundRequest(OutboundRequestKind.NewOrder, new object(), 1UL, "ABC123");
        Assert.Equal(RiskDecisionKind.Allow, (await t.EvaluateAsync(req, default)).Kind);
        Assert.Equal(RiskDecisionKind.Allow, (await t.EvaluateAsync(req, default)).Kind);
        Assert.Equal(RiskDecisionKind.Throttle, (await t.EvaluateAsync(req, default)).Kind);
        var other = req with { ClOrdID = "XYZ999" };
        Assert.Equal(RiskDecisionKind.Allow, (await t.EvaluateAsync(other, default)).Kind);
    }

    [Fact]
    public async Task SecurityIdRateThrottle_LimitsPerSecurity()
    {
        var t = new SecurityIdRateThrottle(maxPerWindow: 1, windowDuration: TimeSpan.FromSeconds(10));
        var r1 = new OutboundRequest(OutboundRequestKind.NewOrder, new object(), 100UL, "A");
        var r2 = new OutboundRequest(OutboundRequestKind.NewOrder, new object(), 200UL, "B");
        Assert.Equal(RiskDecisionKind.Allow, (await t.EvaluateAsync(r1, default)).Kind);
        Assert.Equal(RiskDecisionKind.Throttle, (await t.EvaluateAsync(r1, default)).Kind);
        Assert.Equal(RiskDecisionKind.Allow, (await t.EvaluateAsync(r2, default)).Kind);
    }

    [Fact]
    public void RiskRejectedException_Carries_Decision()
    {
        var ex = new RiskRejectedException(RiskDecision.Reject("nope"));
        Assert.Equal(RiskDecisionKind.Reject, ex.Kind);
        Assert.Contains("nope", ex.Message);
    }
}
