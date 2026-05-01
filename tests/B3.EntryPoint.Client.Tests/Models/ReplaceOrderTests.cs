using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests.Models;

public class ReplaceOrderTests
{
    [Fact]
    public void ReplaceOrderRequest_RoundTrips()
    {
        var req = new ReplaceOrderRequest
        {
            ClOrdID = new ClOrdID("R1"),
            OrigClOrdID = new ClOrdID("O1"),
            SecurityId = 1,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            OrderQty = 10,
            Price = 5.0m,
        };
        Assert.Equal("R1", req.ClOrdID.Value);
        Assert.Equal("O1", req.OrigClOrdID.Value);
    }

    [Fact]
    public void SimpleModifyRequest_RoundTrips()
    {
        var req = new SimpleModifyRequest
        {
            ClOrdID = new ClOrdID("R1"),
            OrigClOrdID = new ClOrdID("O1"),
            SecurityId = 1,
            Side = Side.Sell,
            OrderType = SimpleOrderType.Limit,
            OrderQty = 10,
            Price = 5.0m,
        };
        Assert.Equal(SimpleTimeInForce.Day, req.TimeInForce);
    }

    [Fact]
    public async Task IReplaceOrder_NullRequest_Throws()
    {
        IReplaceOrder client = MakeClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ReplaceAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ReplaceSimpleAsync(null!));
    }

    [Fact]
    public async Task IReplaceOrder_GuardsBeforeEstablished()
    {
        IReplaceOrder client = MakeClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReplaceAsync(new ReplaceOrderRequest
            {
                ClOrdID = new ClOrdID("R"),
                OrigClOrdID = new ClOrdID("O"),
                SecurityId = 1,
                Side = Side.Buy,
                OrderType = OrderType.Limit,
                OrderQty = 1,
            }));
        Assert.Contains("Established", ex.Message);
    }

    private static EntryPointClient MakeClient() => new(new EntryPointClientOptions
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 9999),
        SessionId = 1,
        SessionVerId = 1,
        EnteringFirm = 1,
        Credentials = Credentials.FromUtf8("k"),
    });
}
