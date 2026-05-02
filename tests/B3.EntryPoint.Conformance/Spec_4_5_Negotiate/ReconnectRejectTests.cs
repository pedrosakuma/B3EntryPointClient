using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Fixp;
using B3.Entrypoint.Fixp.Sbe.V6;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.Spec_4_5_Negotiate;

/// <summary>
/// Spec §4.5 — Negative path. Peer rejects the second <c>Establish</c>
/// (the reconnect) with <c>EstablishReject(INVALID_SESSIONVERID)</c>;
/// the client surfaces a <see cref="FixpRejectedException"/>.
/// </summary>
[Trait("Category", "Conformance")]
public class ReconnectRejectTests
{
    [TestPeerOnlyConformanceFact]
    public async Task Reconnect_With_Invalid_SessionVerId_Surfaces_FixpRejected()
    {
        await using var fx = new ConformancePeerFactory(
            TestPeerScenarios.AcceptAll);
        // Reject only the second (reconnect) Establish — the first must succeed.
        fx.Peer.Options.EstablishRejectAfter = 2;
        fx.Peer.Options.EstablishRejectCodeOverride = EstablishRejectCode.INVALID_SESSIONVERID;

        await using var client = new EntryPointClient(fx.ClientOptions);
        await client.ConnectAsync();
        Assert.Equal(FixpClientState.Established, client.State);

        await Assert.ThrowsAsync<FixpRejectedException>(
            async () => await client.ReconnectAsync(fx.ClientOptions.SessionVerId + 1));
    }
}
