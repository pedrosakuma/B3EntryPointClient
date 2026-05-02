using System.Diagnostics;
using System.Linq;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Behaviour tests for the public <see cref="InProcessFixpTestPeer"/> NuGet
/// surface — credentials gating, MessageReceived event, response latency,
/// and scenario dispatch (AcceptAll vs RejectAll).
/// </summary>
public class TestPeerScenarioTests
{
    private static EntryPointClientOptions ClientOptions(InProcessFixpTestPeer peer, uint firm = 7u, string key = "test-key")
        => new()
        {
            Endpoint = peer.LocalEndpoint,
            SessionId = 42u,
            SessionVerId = 1u,
            EnteringFirm = firm,
            Credentials = Credentials.FromUtf8(key),
            KeepAliveIntervalMs = 60_000u,
        };

    [Fact]
    public async Task MessageReceived_FiresForInboundFrames()
    {
        await using var peer = new InProcessFixpTestPeer();
        var seen = new List<ushort>();
        peer.MessageReceived += (_, e) => { lock (seen) seen.Add(e.TemplateId); };
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        // Negotiate (template id 500) + Establish (501) at minimum.
        Assert.NotEmpty(seen);
    }

    [Fact]
    public async Task Credentials_RejectUnknownFirm()
    {
        var opts = new TestPeerOptions
        {
            Credentials = new Dictionary<uint, byte[]> { [99u] = new byte[] { 1, 2, 3 } },
        };
        await using var peer = new InProcessFixpTestPeer(opts);
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer, firm: 7u));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.ConnectAsync(cts.Token));
    }

    [Fact]
    public async Task ResponseLatency_DelaysServerResponses()
    {
        var latency = TimeSpan.FromMilliseconds(150);
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { ResponseLatency = latency });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        sw.Stop();

        // Negotiate + Establish round-trips → at least 2× latency.
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(250),
            $"expected ≥250ms with latency={latency}, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AcceptAllScenario_EmitsExecutionReportNew()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = TestPeerScenarios.AcceptAll });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        await client.SubmitAsync(new NewOrderRequest
        {
            ClOrdID = (ClOrdID)123UL,
            SecurityId = 1001,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = 10m,
            OrderQty = 10,
        }, cts.Token);

        var drainCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var saw = false;
        try
        {
            await foreach (var _ in client.Events().WithCancellation(drainCts.Token))
            { saw = true; break; }
        }
        catch (OperationCanceledException) { }

        Assert.True(saw, "expected at least one inbound event from AcceptAll scenario");
    }

    [Fact]
    public void TestPeerScenarios_BuiltInsAreNonNull()
    {
        Assert.NotNull(TestPeerScenarios.AcceptAll);
        Assert.NotNull(TestPeerScenarios.FillImmediately);
        Assert.NotNull(TestPeerScenarios.RejectAll());
    }

    private static Task SubmitOrderAsync(EntryPointClient client, ulong clOrdId, ulong securityId, ulong qty, decimal price, CancellationToken ct)
        => client.SubmitAsync(new NewOrderRequest
        {
            ClOrdID = (ClOrdID)clOrdId,
            SecurityId = securityId,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = price,
            OrderQty = qty,
        }, ct);

    private static async Task<List<EntryPointEvent>> DrainAsync(EntryPointClient client, int expected, TimeSpan timeout)
    {
        var events = new List<EntryPointEvent>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var ev in client.Events().WithCancellation(cts.Token))
            {
                events.Add(ev);
                if (events.Count >= expected) break;
            }
        }
        catch (OperationCanceledException) { }
        return events;
    }

    [Fact]
    public async Task FillImmediately_EmitsAcceptedThenTrade_FullFill()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = TestPeerScenarios.FillImmediately });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(connectCts.Token);

        await SubmitOrderAsync(client, 555UL, 1001UL, qty: 10UL, price: 12.5m, connectCts.Token);

        var events = await DrainAsync(client, expected: 2, TimeSpan.FromSeconds(2));
        var trade = events.OfType<OrderTrade>().FirstOrDefault();
        Assert.NotNull(trade);
        Assert.Equal(OrderStatus.Filled, trade!.OrderStatus);
        Assert.Equal(10UL, trade.LastQty);
        Assert.Equal(0UL, trade.LeavesQty);
        Assert.Equal(10UL, trade.CumQty);
        Assert.Equal(12.5m, trade.LastPx);
        Assert.Contains(events, e => e is OrderAccepted);
    }

    [Fact]
    public async Task FillImmediately_PartialFill_UsesProvidedFillQty()
    {
        var scenario = new PartialFillScenario(fillQty: 4UL, fillPrice: 11.0m);
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = scenario });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(connectCts.Token);

        await SubmitOrderAsync(client, 600UL, 1002UL, qty: 10UL, price: 9.9m, connectCts.Token);

        var events = await DrainAsync(client, expected: 2, TimeSpan.FromSeconds(2));
        var trade = events.OfType<OrderTrade>().FirstOrDefault();
        Assert.NotNull(trade);
        Assert.Equal(OrderStatus.PartiallyFilled, trade!.OrderStatus);
        Assert.Equal(4UL, trade.LastQty);
        Assert.Equal(6UL, trade.LeavesQty);
        Assert.Equal(4UL, trade.CumQty);
        Assert.Equal(11.0m, trade.LastPx);
    }

    [Fact]
    public async Task RejectAll_EmitsBusinessRejectWithText()
    {
        const string Reason = "test peer rejecting NewOrder";
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = TestPeerScenarios.RejectAll(Reason) });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(connectCts.Token);

        await SubmitOrderAsync(client, 700UL, 1003UL, qty: 5UL, price: 1.0m, connectCts.Token);

        var events = await DrainAsync(client, expected: 1, TimeSpan.FromSeconds(2));
        var bmr = events.OfType<BusinessReject>().FirstOrDefault();
        Assert.NotNull(bmr);
        Assert.Equal(Reason, bmr!.Text);
        // Default RejReason for RejectBusiness without explicit code = 99 (Other).
        Assert.Equal((ushort)99, bmr.RejectReason);
    }

    [Fact]
    public async Task RejectAll_RejectsCancelWithExecutionReportReject()
    {
        const string Reason = "cancel rejected";
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = TestPeerScenarios.RejectAll(Reason) });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(connectCts.Token);

        await client.CancelAsync(new CancelOrderRequest
        {
            ClOrdID = (ClOrdID)801UL,
            OrigClOrdID = (ClOrdID)800UL,
            SecurityId = 2001UL,
            Side = Side.Buy,
        }, connectCts.Token);

        var events = await DrainAsync(client, expected: 1, TimeSpan.FromSeconds(2));
        var rejected = events.OfType<OrderRejected>().FirstOrDefault();
        Assert.NotNull(rejected);
        Assert.Equal((ushort)99, rejected!.RejectCode);
    }

    [Fact]
    public async Task FillImmediatelyScenario_DoesNotRejectCancel()
    {
        // Sanity: built-in FillImmediately scenario does not override OnCancel,
        // so DIM default (Accept) must be exercised via the test peer, producing
        // an OrderCancelled event rather than an OrderRejected.
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = TestPeerScenarios.FillImmediately });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(connectCts.Token);

        await client.CancelAsync(new CancelOrderRequest
        {
            ClOrdID = (ClOrdID)901UL,
            OrigClOrdID = (ClOrdID)900UL,
            SecurityId = 3001UL,
            Side = Side.Sell,
        }, connectCts.Token);

        var events = await DrainAsync(client, expected: 1, TimeSpan.FromSeconds(2));
        Assert.Contains(events, e => e is OrderCancelled);
        Assert.DoesNotContain(events, e => e is OrderRejected);
    }

    [Fact]
    public async Task RejectAll_RejectsModifyWithExecutionReportReject()
    {
        const string Reason = "modify rejected";
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = TestPeerScenarios.RejectAll(Reason) });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(connectCts.Token);

        await client.ReplaceAsync(new ReplaceOrderRequest
        {
            ClOrdID = (ClOrdID)1101UL,
            OrigClOrdID = (ClOrdID)1100UL,
            SecurityId = 4001UL,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = 12.34m,
            OrderQty = 10UL,
        }, connectCts.Token);

        var events = await DrainAsync(client, expected: 1, TimeSpan.FromSeconds(2));
        var rejected = events.OfType<OrderRejected>().FirstOrDefault();
        Assert.NotNull(rejected);
        Assert.Equal((ushort)99, rejected!.RejectCode);
    }

    private sealed class PartialFillScenario : ITestPeerScenario
    {
        private readonly ulong _fillQty;
        private readonly decimal _fillPrice;
        public PartialFillScenario(ulong fillQty, decimal fillPrice) { _fillQty = fillQty; _fillPrice = fillPrice; }
        public NewOrderResponse OnNewOrder(NewOrderContext context) =>
            new NewOrderResponse.AcceptAndFill { FillQty = _fillQty, FillPrice = _fillPrice };
    }
}
