using System.Linq;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.OrderEntry_NewOrder;

/// <summary>
/// Spec — Negative path: peer rejects <c>NewOrder</c> with
/// <c>BusinessMessageReject</c> (template 206), client surfaces a
/// <see cref="BusinessReject"/> event with the original reason text.
/// </summary>
[Trait("Category", "Conformance")]
public class NewOrderRejectTests
{
    [TestPeerOnlyConformanceFact]
    public async Task RejectAll_NewOrder_Surfaces_BusinessReject_With_Text()
    {
        const string reason = "conformance: business-reject text";
        await using var fx = new ConformancePeerFactory(TestPeerScenarios.RejectAll(reason));
        await using var client = new EntryPointClient(fx.ClientOptions);
        await client.ConnectAsync();

        await client.SubmitAsync(new NewOrderRequest
        {
            ClOrdID = (ClOrdID)123UL,
            SecurityId = 1001,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = 1.23m,
            OrderQty = 5,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var evt in client.Events(cts.Token))
        {
            var bmr = Assert.IsType<BusinessReject>(evt);
            Assert.Equal(reason, bmr.Text);
            return;
        }
        throw new TimeoutException("No BusinessReject received");
    }
}
