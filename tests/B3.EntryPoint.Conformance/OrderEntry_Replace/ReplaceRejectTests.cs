using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.OrderEntry_Replace;

/// <summary>
/// Spec — Negative path: peer rejects <c>OrderCancelReplaceRequest</c>
/// with <c>ExecutionReport_Reject</c> carrying
/// <c>CxlRejResponseTo = REPLACE</c>; the client surfaces an
/// <see cref="OrderRejected"/> event.
/// </summary>
[Trait("Category", "Conformance")]
public class ReplaceRejectTests
{
    [TestPeerOnlyConformanceFact]
    public async Task RejectAll_Replace_Surfaces_OrderRejected()
    {
        await using var fx = new ConformancePeerFactory(
            TestPeerScenarios.RejectAll("conformance: replace rejected"));
        await using var client = new EntryPointClient(fx.ClientOptions);
        await client.ConnectAsync();

        await client.ReplaceAsync(new ReplaceOrderRequest
        {
            ClOrdID = (ClOrdID)1101UL,
            OrigClOrdID = (ClOrdID)1100UL,
            SecurityId = 4001UL,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = 12.34m,
            OrderQty = 10UL,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var evt in client.Events(cts.Token))
        {
            var rejected = Assert.IsType<OrderRejected>(evt);
            Assert.Equal((ushort)99, rejected.RejectCode);
            return;
        }
        throw new TimeoutException("No OrderRejected received");
    }
}
