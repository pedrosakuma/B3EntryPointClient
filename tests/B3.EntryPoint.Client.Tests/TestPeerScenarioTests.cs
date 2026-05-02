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

    private sealed class CapturingScenario : ITestPeerScenario
    {
        private readonly ITestPeerScenario _inner;
        public List<OutboundFrameContext> Frames { get; } = new();

        public CapturingScenario(ITestPeerScenario inner) { _inner = inner; }
        public NewOrderResponse OnNewOrder(NewOrderContext context) => _inner.OnNewOrder(context);
        public CancelResponse OnCancel(CancelContext context) => _inner.OnCancel(context);
        public ModifyResponse OnModify(ModifyContext context) => _inner.OnModify(context);

        public OutboundFrameAction OnOutboundFrame(OutboundFrameContext context)
        {
            lock (Frames) Frames.Add(context);
            return new OutboundFrameAction.Send();
        }
    }

    [Fact]
    public async Task OnOutboundFrame_InvokedForEachAppMessage_WithMonotonicSeqNums()
    {
        var capture = new CapturingScenario(TestPeerScenarios.AcceptAll);
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = capture });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        await SubmitOrderAsync(client, 123UL, 1001UL, qty: 1UL, price: 10m, cts.Token);
        await SubmitOrderAsync(client, 124UL, 1001UL, qty: 1UL, price: 10m, cts.Token);

        await DrainAsync(client, expected: 2, TimeSpan.FromSeconds(2));

        List<OutboundFrameContext> snapshot;
        lock (capture.Frames) snapshot = capture.Frames.ToList();

        Assert.True(snapshot.Count >= 2, $"expected ≥2 outbound app frames, got {snapshot.Count}");
        for (int i = 1; i < snapshot.Count; i++)
            Assert.Equal(snapshot[i - 1].MsgSeqNum + 1, snapshot[i].MsgSeqNum);
        Assert.All(snapshot, c => Assert.True(c.FrameLength > 0));
    }

    [Fact]
    public async Task WithSequenceFaults_DropFirstFrame_SuppressesInboundEvent()
    {
        var schedule = new Dictionary<int, OutboundFrameAction>
        {
            [1] = new OutboundFrameAction.Drop(),
        };
        var scenario = TestPeerScenarios.WithSequenceFaults(TestPeerScenarios.AcceptAll, schedule);
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = scenario });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        await SubmitOrderAsync(client, 200UL, 1001UL, qty: 1UL, price: 10m, cts.Token);
        // No second order — only frame is the dropped ER.
        var events = await DrainAsync(client, expected: 1, TimeSpan.FromMilliseconds(400));

        Assert.Empty(events);
    }

    [Fact]
    public async Task WithSequenceFaults_SkipSeq_AdvancesPeerOutboundCounter()
    {
        var capture = new CapturingScenario(TestPeerScenarios.AcceptAll);
        // Capturing wrapper records context for every frame; we layer a SkipSeq
        // on the first frame which should bump OutSeq by 5 — the second frame's
        // MsgSeqNum should therefore jump by 6 (1 from first send + 5 skipped).
        var inner = new ChainedScenario(capture, new Dictionary<int, OutboundFrameAction>
        {
            [1] = new OutboundFrameAction.SkipSeq(5),
        });
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = inner });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        await SubmitOrderAsync(client, 300UL, 1001UL, qty: 1UL, price: 10m, cts.Token);
        await SubmitOrderAsync(client, 301UL, 1001UL, qty: 1UL, price: 10m, cts.Token);

        // Best effort drain — second event may not be delivered to client because
        // SkipSeq creates a wire-level seq jump; the assertion below is on the
        // peer-side capture, not client events.
        try { await DrainAsync(client, expected: 2, TimeSpan.FromMilliseconds(400)); }
        catch (OperationCanceledException) { }

        List<OutboundFrameContext> snapshot;
        lock (capture.Frames) snapshot = capture.Frames.ToList();

        Assert.True(snapshot.Count >= 2);
        var jump = snapshot[1].MsgSeqNum - snapshot[0].MsgSeqNum;
        Assert.Equal(6UL, jump);
    }

    private sealed class ChainedScenario : ITestPeerScenario
    {
        private readonly ITestPeerScenario _capture;
        private readonly IReadOnlyDictionary<int, OutboundFrameAction> _schedule;
        private int _frameCount;

        public ChainedScenario(ITestPeerScenario capture, IReadOnlyDictionary<int, OutboundFrameAction> schedule)
        {
            _capture = capture;
            _schedule = schedule;
        }

        public NewOrderResponse OnNewOrder(NewOrderContext context) => _capture.OnNewOrder(context);
        public CancelResponse OnCancel(CancelContext context) => _capture.OnCancel(context);
        public ModifyResponse OnModify(ModifyContext context) => _capture.OnModify(context);

        public OutboundFrameAction OnOutboundFrame(OutboundFrameContext context)
        {
            _capture.OnOutboundFrame(context);
            var idx = System.Threading.Interlocked.Increment(ref _frameCount);
            return _schedule.TryGetValue(idx, out var a) ? a : new OutboundFrameAction.Send();
        }
    }
}
