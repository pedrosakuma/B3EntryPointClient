using System.Net;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Conformance.Infrastructure;

/// <summary>
/// Resolves a B3 EntryPoint peer endpoint + credentials from environment
/// variables. The same wire-puro client targets either a local
/// <c>B3MatchingPlatform</c> instance or B3 UAT — only env values change.
/// </summary>
/// <remarks>
/// <para>
/// Scenarios that require a peer must call <see cref="TryResolve"/> and
/// <c>Skip</c> when <c>null</c>, so CI is green even without a peer
/// configured. This is the "expected to fail until B3MatchingPlatform#42 lands"
/// behavior described in the bootstrap issue.
/// </para>
/// <para>
/// Set <c>ENTRYPOINT_TESTPEER=1</c> to spin up an in-process
/// <see cref="InProcessFixpTestPeer"/> instead. This makes the conformance suite
/// runnable in CI without an external endpoint.
/// </para>
/// </remarks>
public sealed record PeerEndpoint(IPEndPoint Endpoint, uint SessionId, uint SessionVerId, uint EnteringFirm, string AccessKey)
{
    public const string EnvEndpoint = "B3EP_PEER";
    public const string EnvSessionId = "B3EP_SESSION_ID";
    public const string EnvSessionVerId = "B3EP_SESSION_VER_ID";
    public const string EnvFirm = "B3EP_FIRM";
    public const string EnvAccessKey = "B3EP_ACCESS_KEY";
    public const string EnvUseTestPeer = "ENTRYPOINT_TESTPEER";

    private static readonly Lazy<InProcessFixpTestPeer?> SharedTestPeer = new(() =>
    {
        if (!IsTestPeerEnabled()) return null;
        var peer = new InProcessFixpTestPeer();
        peer.Start();
        AppDomain.CurrentDomain.ProcessExit += async (_, _) => await peer.DisposeAsync();
        return peer;
    });

    public static bool IsTestPeerEnabled()
    {
        var v = Environment.GetEnvironmentVariable(EnvUseTestPeer);
        return string.Equals(v, "1", StringComparison.Ordinal)
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static PeerEndpoint? TryResolve()
    {
        if (IsTestPeerEnabled() && SharedTestPeer.Value is { } peer)
        {
            return new PeerEndpoint(peer.Endpoint, SessionId: 1u, SessionVerId: 1u, EnteringFirm: 1u, AccessKey: "TESTPEER");
        }

        var endpoint = Environment.GetEnvironmentVariable(EnvEndpoint);
        var sessionId = Environment.GetEnvironmentVariable(EnvSessionId);
        var sessionVerId = Environment.GetEnvironmentVariable(EnvSessionVerId);
        var firm = Environment.GetEnvironmentVariable(EnvFirm);
        var accessKey = Environment.GetEnvironmentVariable(EnvAccessKey);

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(sessionVerId) ||
            string.IsNullOrWhiteSpace(firm) ||
            string.IsNullOrWhiteSpace(accessKey))
        {
            return null;
        }

        var parts = endpoint.Split(':');
        if (parts.Length != 2) return null;
        var addresses = Dns.GetHostAddresses(parts[0]);
        if (addresses.Length == 0) return null;
        var ep = new IPEndPoint(addresses[0], int.Parse(parts[1]));

        return new PeerEndpoint(ep, uint.Parse(sessionId), uint.Parse(sessionVerId), uint.Parse(firm), accessKey);
    }

    public const string SkipReason =
        "Conformance peer not configured. Set ENTRYPOINT_TESTPEER=1 for the in-process peer, or B3EP_PEER + credentials for an external one.";
}
