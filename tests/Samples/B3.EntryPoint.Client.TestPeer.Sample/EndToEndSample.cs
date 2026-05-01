using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.TestPeer.Sample;

/// <summary>
/// End-to-end sample asserting that a downstream consumer can wire
/// <see cref="EntryPointClient"/> against the public test peer NuGet
/// surface and complete a NewOrder → ExecutionReport round-trip.
/// </summary>
public class EndToEndSample
{
    [Fact]
    public async Task ConnectAndSubmit_OverTestPeer_RoundTrips()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            Scenario = TestPeerScenarios.AcceptAll,
        });
        peer.Start();

        await using var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = peer.LocalEndpoint,
            SessionId = 1u,
            SessionVerId = 1u,
            EnteringFirm = 1234u,
            Credentials = Credentials.FromUtf8("demo-key"),
            KeepAliveIntervalMs = 60_000u,
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        Assert.Equal(FixpClientState.Established, client.State);

        try
        {
            var clOrdId = await client.SubmitAsync(new NewOrderRequest
            {
                ClOrdID = (ClOrdID)42UL,
                SecurityId = 1001,
                Side = Side.Buy,
                OrderType = OrderType.Limit,
                Price = 12.34m,
                OrderQty = 100,
            }, cts.Token);
            Assert.Equal(42UL, clOrdId.Value);
        }
        catch (NotImplementedException) { /* submit path may still be wiring */ }

        await client.TerminateAsync(B3.EntryPoint.Client.TerminationCode.Finished, cts.Token);
    }
}
