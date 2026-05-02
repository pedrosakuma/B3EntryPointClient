using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Conformance.Infrastructure;

/// <summary>
/// Spins up a per-test in-process FIXP peer with a custom
/// <see cref="ITestPeerScenario"/> and returns matching client options.
/// Use only when <see cref="PeerEndpoint.IsTestPeerEnabled"/> is true (i.e.
/// from a <see cref="TestPeerOnlyConformanceFactAttribute"/> test).
/// </summary>
public sealed class ConformancePeerFactory : IAsyncDisposable
{
    private readonly InProcessFixpTestPeer _peer;

    public InProcessFixpTestPeer Peer => _peer;
    public EntryPointClientOptions ClientOptions { get; }

    public ConformancePeerFactory(ITestPeerScenario scenario, uint sessionId = 1u, uint sessionVerId = 1u, uint enteringFirm = 1u, string accessKey = "TESTPEER")
    {
        _peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = scenario });
        _peer.Start();
        ClientOptions = new EntryPointClientOptions
        {
            Endpoint = _peer.LocalEndpoint!,
            SessionId = sessionId,
            SessionVerId = sessionVerId,
            EnteringFirm = enteringFirm,
            Credentials = Credentials.FromUtf8(accessKey),
        };
    }

    public ValueTask DisposeAsync() => _peer.DisposeAsync();
}
