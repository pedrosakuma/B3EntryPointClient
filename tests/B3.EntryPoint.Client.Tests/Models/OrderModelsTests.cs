using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests.Models;

public class OrderModelsTests
{
    [Fact]
    public void ClOrdID_RejectsZero() =>
        Assert.Throws<ArgumentException>(() => new ClOrdID(0UL));

    [Fact]
    public void ClOrdID_Parse_RoundTrips()
    {
        var id = ClOrdID.Parse("12345");
        Assert.Equal(12345UL, id.Value);
        Assert.Equal("12345", id.ToString());
    }

    [Fact]
    public void ClOrdID_AcceptsArbitraryUlong()
    {
        var id = new ClOrdID(ulong.MaxValue);
        ulong raw = id;
        Assert.Equal(ulong.MaxValue, raw);
    }

    [Fact]
    public void NewOrderRequest_RoundTripsRequiredFields()
    {
        var req = new NewOrderRequest
        {
            ClOrdID = new ClOrdID(3UL),
            SecurityId = 12345,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            OrderQty = 100,
            Price = 12.34m,
        };
        Assert.Equal(3UL, req.ClOrdID.Value);
        Assert.Equal(TimeInForce.Day, req.TimeInForce);
        Assert.Equal(AccountType.RegularAccount, req.AccountType);
    }

    [Fact]
    public void SimpleNewOrderRequest_RoundTrips()
    {
        var req = new SimpleNewOrderRequest
        {
            ClOrdID = new ClOrdID(4UL),
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
                ClOrdID = new ClOrdID(5UL),
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
