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
            CrossType = CrossType.Default,
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
        var qr = new QuoteRequestMessage { QuoteReqId = "QR-1", SecurityId = 1 };
        var q = new QuoteMessage { QuoteId = "Q-1", SecurityId = 1, BidPrice = 1m, OfferPrice = 2m };
        Assert.Equal("QR-1", qr.QuoteReqId);
        Assert.Equal("Q-1", q.QuoteId);
    }
}
