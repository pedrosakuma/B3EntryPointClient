using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.OrderEntry_Replace;

[Trait("Category", "Conformance")]
public class ReplaceHappyPathTests
{
    [ConformanceFact]
    public async Task Replace_OpenOrder_Receives_OrderModified()
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
        var clOrdId = new ClOrdID((ulong)(uint)Guid.NewGuid().GetHashCode() | 1UL);
        await client.ReplaceAsync(new ReplaceOrderRequest
        {
            ClOrdID = clOrdId,
            OrigClOrdID = new ClOrdID(2UL),
            SecurityId = 1,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            OrderQty = 2,
            Price = 0.02m,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var evt in client.Events(cts.Token))
        {
            var modified = Assert.IsType<OrderModified>(evt);
            Assert.Equal(clOrdId.Value, modified.ClOrdID.Value);
            return;
        }
        throw new TimeoutException("No OrderModified received");
    }
}
