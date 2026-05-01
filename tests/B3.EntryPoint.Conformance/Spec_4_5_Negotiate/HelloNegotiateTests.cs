using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.Spec_4_5_Negotiate;

/// <summary>
/// Spec §4.5 — Negotiation. Bootstrap "Hello-Negotiate": connect to a B3
/// EntryPoint peer, complete <c>Negotiate → NegotiateResponse</c> and
/// <c>Establish → EstablishmentAck</c>, then <c>Terminate</c> cleanly.
/// </summary>
[Trait("Category", "Conformance")]
public class HelloNegotiateTests
{
    [ConformanceFact]
    public async Task Negotiate_Establish_RoundTrip()
    {
        var peer = PeerEndpoint.TryResolve()!;

        var options = new EntryPointClientOptions
        {
            Endpoint = peer.Endpoint,
            SessionId = peer.SessionId,
            SessionVerId = peer.SessionVerId,
            EnteringFirm = peer.EnteringFirm,
            Credentials = Credentials.FromUtf8(peer.AccessKey),
        };

        await using var client = new EntryPointClient(options);
        await client.ConnectAsync(CancellationToken.None);

        Assert.Equal(FixpClientState.Established, client.State);
    }
}
