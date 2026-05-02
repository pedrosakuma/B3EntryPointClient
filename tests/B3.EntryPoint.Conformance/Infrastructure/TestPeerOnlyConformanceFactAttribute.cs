using Xunit.Sdk;

namespace B3.EntryPoint.Conformance.Infrastructure;

/// <summary>
/// Marks a conformance test that requires a configurable in-process peer
/// (e.g. to drive reject paths), so it must be skipped when the suite is
/// pointed at a real B3 endpoint via <c>B3EP_PEER</c>. Tests that use this
/// attribute typically construct their own <c>InProcessFixpTestPeer</c> with
/// a custom <c>ITestPeerScenario</c> via
/// <see cref="ConformancePeerFactory"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class TestPeerOnlyConformanceFactAttribute : FactAttribute
{
    public TestPeerOnlyConformanceFactAttribute()
    {
        if (!PeerEndpoint.IsTestPeerEnabled())
            Skip = "Requires ENTRYPOINT_TESTPEER=1 (test-peer-only scenario).";
    }
}
