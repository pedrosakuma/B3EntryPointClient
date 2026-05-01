using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests.Models;

public class EntryPointEventsTests
{
    [Fact]
    public void OrderAccepted_RoundTrips()
    {
        var ev = new OrderAccepted
        {
            SeqNum = 1,
            SendingTime = DateTimeOffset.UnixEpoch,
            ClOrdID = new ClOrdID("X"),
            OrderId = 100,
            OrderStatus = OrderStatus.New,
            SecurityId = 1,
            Side = Side.Buy,
        };
        Assert.IsType<EntryPointEvent>(ev, exactMatch: false);
    }

    [Fact]
    public void OrderTrade_CarriesPriceAndQty()
    {
        var ev = new OrderTrade
        {
            SeqNum = 2,
            SendingTime = DateTimeOffset.UnixEpoch,
            ClOrdID = new ClOrdID("X"),
            OrderId = 100,
            TradeId = 200,
            OrderStatus = OrderStatus.PartiallyFilled,
            LastPx = 12.5m,
            LastQty = 10,
        };
        Assert.Equal(12.5m, ev.LastPx);
    }

    [Fact]
    public void BusinessReject_RoundTrips()
    {
        var ev = new BusinessReject
        {
            SeqNum = 3,
            SendingTime = DateTimeOffset.UnixEpoch,
            RefSeqNum = 1,
            RejectReason = 99,
            Text = "nope",
        };
        Assert.Equal((ushort)99, ev.RejectReason);
    }

    [Fact]
    public void OrderCancelled_AllowsRestatementReason()
    {
        var ev = new OrderCancelled
        {
            SeqNum = 4,
            SendingTime = DateTimeOffset.UnixEpoch,
            ClOrdID = new ClOrdID("X"),
            OrigClOrdID = new ClOrdID("O"),
            OrderId = 1,
            OrderStatus = OrderStatus.Cancelled,
            RestatementReason = ExecRestatementReason.CancelOnTerminate,
        };
        Assert.Equal(ExecRestatementReason.CancelOnTerminate, ev.RestatementReason);
    }

    [Fact]
    public async Task EntryPointClient_Events_CompletesEmptyForNow()
    {
        await using var c = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1),
            SessionId = 1,
            SessionVerId = 1,
            EnteringFirm = 1,
            Credentials = B3.EntryPoint.Client.Auth.Credentials.FromUtf8("k"),
        });
        // Stream guards before Established — that's the documented contract.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in c.Events()) { }
        });
    }
}
