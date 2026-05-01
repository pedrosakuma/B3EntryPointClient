using Xunit.Sdk;

namespace B3.EntryPoint.Conformance.Infrastructure;

/// <summary>
/// xUnit v2 doesn't ship dynamic skip; this attribute marks a test as skipped
/// at discovery time when the conformance peer env vars are not set, so CI
/// stays green without a peer configured.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("Xunit.Sdk.FactDiscoverer", "xunit.execution.{Platform}")]
public sealed class ConformanceFactAttribute : FactAttribute
{
    public ConformanceFactAttribute()
    {
        if (PeerEndpoint.TryResolve() is null)
            Skip = PeerEndpoint.SkipReason;
    }
}
