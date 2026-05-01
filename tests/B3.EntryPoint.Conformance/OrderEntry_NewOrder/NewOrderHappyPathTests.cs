using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.OrderEntry_NewOrder;

/// <summary>Order Entry happy-path: <c>NewOrderSingle</c> → <c>OrderAccepted</c>.</summary>
[Trait("Category", "Conformance")]
public class NewOrderHappyPathTests
{
    [ConformanceFact]
    public async Task Submit_NewOrder_Receives_OrderAccepted()
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
        await client.SubmitAsync(new NewOrderRequest
        {
            ClOrdID = clOrdId,
            SecurityId = 1,
            Side = Side.Buy,
            OrderQty = 1,
            Price = 0.01m,
            OrderType = OrderType.Limit,
            TimeInForce = TimeInForce.Day,
            Account = 1,
        });

        var evt = await ReadOneAsync(client, TimeSpan.FromSeconds(2));
        var accepted = Assert.IsType<OrderAccepted>(evt);
        Assert.Equal(clOrdId.Value, accepted.ClOrdID.Value);
    }

    private static async Task<EntryPointEvent> ReadOneAsync(EntryPointClient client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await foreach (var evt in client.Events(cts.Token))
            return evt;
        throw new TimeoutException("No event received");
    }
}
