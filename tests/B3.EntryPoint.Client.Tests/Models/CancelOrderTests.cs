using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests.Models;

public class CancelOrderTests
{
    [Fact]
    public void CancelOrderRequest_RoundTrips()
    {
        var req = new CancelOrderRequest
        {
            ClOrdID = new ClOrdID(2UL),
            OrigClOrdID = new ClOrdID(3UL),
            SecurityId = 1,
            Side = Side.Buy,
        };
        Assert.Equal(2UL, req.ClOrdID.Value);
        Assert.Equal(3UL, req.OrigClOrdID.Value);
    }

    [Fact]
    public void MassActionRequest_DefaultsScopeToTradingSession()
    {
        var req = new MassActionRequest
        {
            ClOrdID = new ClOrdID(4UL),
            ActionType = MassActionType.CancelOrders,
        };
        Assert.Equal(MassActionScope.AllOrdersForATradingSession, req.Scope);
    }

    [Fact]
    public void MassActionReport_RoundTrips()
    {
        var rpt = new MassActionReport
        {
            ClOrdID = new ClOrdID(5UL),
            Response = MassActionResponse.Accepted,
            ActionType = MassActionType.CancelOrders,
            Scope = MassActionScope.AllOrdersForATradingSession,
            TotalAffectedOrders = 17,
        };
        Assert.Equal(17u, rpt.TotalAffectedOrders);
    }

    [Fact]
    public async Task ICancelOrder_NullRequest_Throws()
    {
        ICancelOrder client = MakeClient();
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CancelAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.MassActionAsync(null!));
    }

    [Fact]
    public async Task ICancelOrder_GuardsBeforeEstablished()
    {
        ICancelOrder client = MakeClient();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CancelAsync(new CancelOrderRequest
            {
                ClOrdID = new ClOrdID(6UL),
                OrigClOrdID = new ClOrdID(7UL),
                SecurityId = 1,
                Side = Side.Buy,
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
