using System.Net;

namespace B3.EntryPoint.Conformance.Infrastructure;

/// <summary>
/// Resolves a B3 EntryPoint peer endpoint + credentials from environment
/// variables. The same wire-puro client targets either a local
/// <c>B3MatchingPlatform</c> instance or B3 UAT — only env values change.
/// </summary>
/// <remarks>
/// Scenarios that require a peer must call <see cref="TryResolve"/> and
/// <c>Skip</c> when <c>null</c>, so CI is green even without a peer
/// configured. This is the "expected to fail until B3MatchingPlatform#42 lands"
/// behavior described in the bootstrap issue.
/// </remarks>
public sealed record PeerEndpoint(IPEndPoint Endpoint, uint SessionId, uint SessionVerId, uint EnteringFirm, string AccessKey)
{
    public const string EnvEndpoint = "B3EP_PEER";
    public const string EnvSessionId = "B3EP_SESSION_ID";
    public const string EnvSessionVerId = "B3EP_SESSION_VER_ID";
    public const string EnvFirm = "B3EP_FIRM";
    public const string EnvAccessKey = "B3EP_ACCESS_KEY";

    public static PeerEndpoint? TryResolve()
    {
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
        "Conformance peer not configured. Set B3EP_PEER, B3EP_SESSION_ID, B3EP_SESSION_VER_ID, B3EP_FIRM, B3EP_ACCESS_KEY to run.";
}
