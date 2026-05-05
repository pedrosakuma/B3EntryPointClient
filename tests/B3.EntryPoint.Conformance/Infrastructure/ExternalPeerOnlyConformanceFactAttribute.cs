using Xunit.Sdk;

namespace B3.EntryPoint.Conformance.Infrastructure;

/// <summary>
/// Marks a conformance test that requires a real external peer (e.g.
/// <c>B3MatchingPlatform</c> or B3 UAT) because the in-process
/// <see cref="B3.EntryPoint.Client.TestPeer.InProcessFixpTestPeer"/>
/// does not model the message family under test (e.g. Cross / Quote flows).
/// Skipped both when no peer env vars are set AND when only
/// <c>ENTRYPOINT_TESTPEER=1</c> is set, so CI stays green.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class ExternalPeerOnlyConformanceFactAttribute : FactAttribute
{
    public ExternalPeerOnlyConformanceFactAttribute()
    {
        if (PeerEndpoint.IsTestPeerEnabled())
        {
            Skip = "Requires an external peer (B3MatchingPlatform / B3 UAT). The in-process TestPeer does not model this flow.";
            return;
        }
        if (PeerEndpoint.TryResolve() is null)
            Skip = PeerEndpoint.SkipReason;
    }
}
