using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.OrderEntry_Cancel;

[Trait("Category", "Conformance")]
public class CancelHappyPathTests
{
    [ConformanceFact]
    public async Task Cancel_OpenOrder_Receives_OrderCancelled()
    {
        var peer = PeerEndpoint.TryResolve()!;
        await using var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = peer.Endpoint,
            SessionId = peer.SessionId,
            SessionVerId = peer.SessionVerId,
            EnteringFirm = peer.EnteringFirm,
            Credentials = Credentials.FromUtf8(peer.AccessKey),
        });

        await client.ConnectAsync();
        await client.CancelAsync(new CancelOrderRequest
        {
            ClOrdID = new ClOrdID($"C-{Guid.NewGuid():N}".Substring(0, 20)),
            OrigClOrdID = new ClOrdID("ORIG"),
            SecurityId = 1,
            Side = Side.Buy,
        });
    }
}
