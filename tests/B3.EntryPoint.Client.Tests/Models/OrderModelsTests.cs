using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests.Models;

public class OrderModelsTests
{
    [Fact]
    public void ClOrdID_RejectsEmpty() =>
        Assert.Throws<ArgumentException>(() => new ClOrdID(""));

    [Fact]
    public void ClOrdID_RejectsTooLong() =>
        Assert.Throws<ArgumentException>(() => new ClOrdID(new string('A', 21)));

    [Fact]
    public void ClOrdID_AcceptsUpToMax()
    {
        var id = new ClOrdID(new string('A', 20));
        Assert.Equal(20, id.Value.Length);
        string s = id;
        Assert.Equal(id.Value, s);
    }

    [Fact]
    public void NewOrderRequest_RoundTripsRequiredFields()
    {
        var req = new NewOrderRequest
        {
            ClOrdID = new ClOrdID("ORD-1"),
            SecurityId = 12345,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            OrderQty = 100,
            Price = 12.34m,
        };
        Assert.Equal("ORD-1", req.ClOrdID.Value);
        Assert.Equal(TimeInForce.Day, req.TimeInForce);
        Assert.Equal(AccountType.RegularAccount, req.AccountType);
    }

    [Fact]
    public void SimpleNewOrderRequest_RoundTrips()
    {
        var req = new SimpleNewOrderRequest
        {
            ClOrdID = new ClOrdID("S1"),
            SecurityId = 1,
            Side = Side.Sell,
            OrderType = SimpleOrderType.Market,
            OrderQty = 10,
        };
        Assert.Equal(SimpleTimeInForce.Day, req.TimeInForce);
    }

    [Fact]
    public async Task ISubmitOrder_Submit_GuardsBeforeEstablished()
    {
        ISubmitOrder client = MakeClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SubmitAsync(new NewOrderRequest
            {
                ClOrdID = new ClOrdID("X"),
                SecurityId = 1,
                Side = Side.Buy,
                OrderType = OrderType.Market,
                OrderQty = 1,
            }));
        Assert.Contains("Established", ex.Message);
    }

    [Fact]
    public async Task ISubmitOrder_SubmitSimple_NullRequest_Throws()
    {
        ISubmitOrder client = MakeClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.SubmitSimpleAsync(null!));
    }

    private static EntryPointClient MakeClient() => new(new EntryPointClientOptions
    {
        Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9999),
        SessionId = 1,
        SessionVerId = 1,
        EnteringFirm = 1,
        Credentials = B3.EntryPoint.Client.Auth.Credentials.FromUtf8("k"),
    });
}
