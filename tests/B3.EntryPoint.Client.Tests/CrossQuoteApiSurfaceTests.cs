using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Models;
using Xunit;

namespace B3.EntryPoint.Client.Tests;

public class CrossQuoteApiSurfaceTests
{
    [Fact]
    public void EntryPointClient_ImplementsCrossAndQuoteInterfaces()
    {
        Assert.True(typeof(ISubmitCross).IsAssignableFrom(typeof(EntryPointClient)));
        Assert.True(typeof(IQuoteFlow).IsAssignableFrom(typeof(EntryPointClient)));
    }

    [Fact]
    public void NewOrderCrossRequest_IsConstructible()
    {
        var req = new NewOrderCrossRequest
        {
            CrossId = "X-1",
            SecurityId = 5,
            CrossType = CrossType.AllOrNone,
            Prioritization = CrossPrioritization.None,
            Price = 12.34m,
            Legs = new[]
            {
                new CrossLeg { ClOrdID = (ClOrdID)1UL, Side = Side.Buy, OrderQty = 10 },
                new CrossLeg { ClOrdID = (ClOrdID)2UL, Side = Side.Sell, OrderQty = 10 },
            },
        };
        Assert.Equal(2, req.Legs.Count);
    }

    [Fact]
    public void QuoteAndQuoteRequest_AreConstructible()
    {
        var qr = new QuoteRequestMessage
        {
            QuoteReqId = "1001",
            SecurityId = 1,
            Side = Side.Buy,
            Price = 1m,
            OrderQty = 100,
            SettlType = SettlementType.Mutual,
            DaysToSettlement = 30,
            ContraBroker = 7,
        };
        var q = new QuoteMessage
        {
            QuoteId = "2001",
            SecurityId = 1,
            Side = Side.Sell,
            OrderQty = 100,
            SettlType = SettlementType.Mutual,
            DaysToSettlement = 30,
            Price = 2m,
        };
        Assert.Equal("1001", qr.QuoteReqId);
        Assert.Equal("2001", q.QuoteId);
    }
}
