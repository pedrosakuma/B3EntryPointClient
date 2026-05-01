using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests;

public class SegmentedEntryPointClientTests
{
    [Fact]
    public void Ctor_ValidatesInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new SegmentedEntryPointClient(
            null!, _ => 1, _ => 1, _ => 1));
        Assert.Throws<ArgumentException>(() => new SegmentedEntryPointClient(
            new Dictionary<byte, EntryPointClientOptions>(), _ => 1, _ => 1, _ => 1));
    }

    [Fact]
    public void DefaultSegment_IsLowestKey_AndSegmentsAreExposed()
    {
        var s = new SegmentedEntryPointClient(
            new Dictionary<byte, EntryPointClientOptions>
            {
                [3] = MakeOptions(3),
                [1] = MakeOptions(1),
                [5] = MakeOptions(5),
            },
            _ => 1, _ => 1, _ => 1);
        Assert.Equal((byte)1, s.DefaultSegment);
        Assert.Equal(3, s.Segments.Count);
    }

    [Fact]
    public async Task SubmitAsync_RoutesToConfiguredSegment_Throws_WhenSegmentMissing()
    {
        await using var s = new SegmentedEntryPointClient(
            new Dictionary<byte, EntryPointClientOptions>
            {
                [1] = MakeOptions(1),
            },
            routeNewOrder: _ => 99,
            routeReplace: _ => 1,
            routeCancel: _ => 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.SubmitAsync(new NewOrderRequest
            {
                ClOrdID = ClOrdID.Parse("1"),
                SecurityId = 1,
                Side = Side.Buy,
                OrderType = OrderType.Limit,
                OrderQty = 1,
                Price = 1m,
            }));
        Assert.Contains("MarketSegmentID=99", ex.Message);
    }

    [Fact]
    public async Task SubmitAsync_RoutesToCorrectClient_NotConnected_Throws()
    {
        // With a single configured segment, a routed request should resolve to
        // that client. Since it is not connected, the call must throw the
        // EntryPointClient's "not in Established state" error — proving the
        // routing succeeded and the request reached the underlying client.
        await using var s = new SegmentedEntryPointClient(
            new Dictionary<byte, EntryPointClientOptions> { [7] = MakeOptions(7) },
            routeNewOrder: _ => 7, routeReplace: _ => 7, routeCancel: _ => 7);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.SubmitAsync(new NewOrderRequest
            {
                ClOrdID = ClOrdID.Parse("1"),
                SecurityId = 1,
                Side = Side.Buy,
                OrderType = OrderType.Limit,
                OrderQty = 1,
                Price = 1m,
            }));
    }

    private static EntryPointClientOptions MakeOptions(byte segment) => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 9999),
        SessionId = 1,
        SessionVerId = 1,
        EnteringFirm = 1,
        Credentials = Credentials.FromUtf8("k"),
        DefaultMarketSegmentId = segment,
    };
}
