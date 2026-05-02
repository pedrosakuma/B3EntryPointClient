using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.OrderEntry_Cancel;

/// <summary>
/// Spec — Negative path: peer rejects <c>OrderCancelRequest</c> with
/// <c>ExecutionReport_Reject</c> (template 204) carrying
/// <c>CxlRejResponseTo = CANCEL</c>; the client surfaces an
/// <see cref="OrderRejected"/> event.
/// </summary>
[Trait("Category", "Conformance")]
public class CancelRejectTests
{
    [TestPeerOnlyConformanceFact]
    public async Task RejectAll_Cancel_Surfaces_OrderRejected()
    {
        await using var fx = new ConformancePeerFactory(
            TestPeerScenarios.RejectAll("conformance: cancel rejected"));
        await using var client = new EntryPointClient(fx.ClientOptions);
        await client.ConnectAsync();

        await client.CancelAsync(new CancelOrderRequest
        {
            ClOrdID = (ClOrdID)801UL,
            OrigClOrdID = (ClOrdID)800UL,
            SecurityId = 2001UL,
            Side = Side.Buy,
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
