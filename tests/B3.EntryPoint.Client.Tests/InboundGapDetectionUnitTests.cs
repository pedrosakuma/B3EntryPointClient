using System.Net;
using System.Reflection;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Unit tests for the inbound gap detection introduced in #138. Drives
/// <c>OnInboundEventForPersistence</c> directly via the
/// <c>HandleInboundEventForTesting</c> internal hook with a stub
/// <see cref="RetransmitRequestHandler"/> bound via
/// <c>BindRetransmitForTesting</c>, so the tests do not require a live FIXP
/// session.
/// </summary>
public class InboundGapDetectionUnitTests
{
    private static EntryPointClientOptions BaseOptions() => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
        SessionId = 42u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("k"),
    };

    private static RetransmitRequestHandler StubHandler(List<(ulong from, uint count)> calls)
    {
        Task SendAsync(ulong from, uint count, CancellationToken ct)
        {
            lock (calls) calls.Add((from, count));
            return Task.CompletedTask;
        }
        var ctor = typeof(RetransmitRequestHandler).GetConstructors(
            BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 1);
        return (RetransmitRequestHandler)ctor.Invoke(new object?[]
        {
            (Func<ulong, uint, CancellationToken, Task>)SendAsync,
        });
    }

    private static EntryPointEvent FakeAck(ulong seq) => new OrderAccepted
    {
        SeqNum = seq,
        SendingTime = DateTimeOffset.UnixEpoch,
        ClOrdID = new ClOrdID(seq),
        OrderId = seq,
        OrderStatus = OrderStatus.New,
        SecurityId = 1UL,
        Side = Side.Buy,
    };

    [Fact]
    public async Task InSessionGap_EmitsRetransmitRequest()
    {
        var calls = new List<(ulong from, uint count)>();
        var handler = StubHandler(calls);

        var client = new EntryPointClient(BaseOptions());
        client.BindRetransmitForTesting(handler);

        var requested = new TaskCompletionSource<RetransmitRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        handler.RetransmitRequested += (_, e) => requested.TrySetResult(e);

        // Feed seqs [1, 2, 5] — gap is [3, 4].
        client.HandleInboundEventForTesting(FakeAck(1));
        client.HandleInboundEventForTesting(FakeAck(2));
        client.HandleInboundEventForTesting(FakeAck(5));

        var args = await requested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3UL, args.FromSeqNo);
        Assert.Equal(2u, args.Count);

        // Wait for the async send dispatched from OnInboundEventForPersistence
        // (Task.Run) to actually invoke the stub.
        for (var i = 0; i < 50 && calls.Count == 0; i++)
            await Task.Delay(10);
        Assert.Single(calls);
        Assert.Equal((3UL, 2u), calls[0]);

        var state = client.GetInboundGapStateForTesting();
        Assert.Equal(2UL, state.contiguous);
        Assert.Equal(5UL, state.highest);
        Assert.Equal(1, state.pending);
        Assert.True(state.gapInFlight);
    }

    [Fact]
    public async Task GapFilledByRetransmission_AdvancesContiguousAndClearsInFlight()
    {
        var calls = new List<(ulong from, uint count)>();
        var handler = StubHandler(calls);
        var client = new EntryPointClient(BaseOptions());
        client.BindRetransmitForTesting(handler);

        client.HandleInboundEventForTesting(FakeAck(1));
        client.HandleInboundEventForTesting(FakeAck(2));
        client.HandleInboundEventForTesting(FakeAck(5));

        // Wait until the gap-detect dispatch has fired so _gapRequestInFlight
        // is observably set.
        for (var i = 0; i < 50 && calls.Count == 0; i++)
            await Task.Delay(10);

        // Retransmitted frames arrive in order — contiguous tail catches up
        // to the running max (5).
        client.HandleInboundEventForTesting(FakeAck(3));
        client.HandleInboundEventForTesting(FakeAck(4));

        var state = client.GetInboundGapStateForTesting();
        Assert.Equal(5UL, state.contiguous);
        Assert.Equal(5UL, state.highest);
        Assert.Equal(0, state.pending);
        Assert.False(state.gapInFlight);

        // No additional RetransmitRequest while only one gap was outstanding.
        Assert.Single(calls);
    }

    [Fact]
    public async Task ConcurrentGaps_AreCappedAtOneInFlight()
    {
        var calls = new List<(ulong from, uint count)>();
        var handler = StubHandler(calls);
        var client = new EntryPointClient(BaseOptions());
        client.BindRetransmitForTesting(handler);

        // Two non-contiguous gaps in quick succession: [3] missing, then [6,7]
        // missing. Only one RetransmitRequest should be in flight at a time.
        client.HandleInboundEventForTesting(FakeAck(1));
        client.HandleInboundEventForTesting(FakeAck(2));
        client.HandleInboundEventForTesting(FakeAck(4));
        client.HandleInboundEventForTesting(FakeAck(5));
        client.HandleInboundEventForTesting(FakeAck(8));

        for (var i = 0; i < 50 && calls.Count == 0; i++)
            await Task.Delay(10);

        Assert.Single(calls);
        Assert.Equal((3UL, 1u), calls[0]);
    }

    [Fact]
    public void DuplicateSeqs_AreIgnoredForAdvancement()
    {
        var calls = new List<(ulong from, uint count)>();
        var handler = StubHandler(calls);
        var client = new EntryPointClient(BaseOptions());
        client.BindRetransmitForTesting(handler);

        client.HandleInboundEventForTesting(FakeAck(1));
        client.HandleInboundEventForTesting(FakeAck(2));
        client.HandleInboundEventForTesting(FakeAck(2)); // duplicate
        client.HandleInboundEventForTesting(FakeAck(1)); // duplicate

        var state = client.GetInboundGapStateForTesting();
        Assert.Equal(2UL, state.contiguous);
        Assert.Equal(2UL, state.highest);
        Assert.False(state.gapInFlight);
        Assert.Empty(calls);
    }

    [Fact]
    public void InboundGapAtReconnectEvent_FiresWithExpectedArgs()
    {
        var client = new EntryPointClient(BaseOptions());
        InboundGapAtReconnectEventArgs? captured = null;
        client.InboundGapAtReconnect += (_, e) => captured = e;

        client.RaiseInboundGapAtReconnectForTesting(fromSeqNo: 3UL, count: 1u, priorSessionVerId: 5UL);

        Assert.NotNull(captured);
        Assert.Equal(3UL, captured!.FromSeqNo);
        Assert.Equal(1u, captured.Count);
        Assert.Equal(5UL, captured.PriorSessionVerId);
    }

    [Fact]
    public void GapState_CapturedForReconnect_ReportsCorrectMissingCount()
    {
        // Simulates the snapshot the ReconnectAsync prelude takes: contiguous=2,
        // highest=5, pending=[4,5] -> missing = (5-2) - 2 = 1 (seq 3 only).
        var client = new EntryPointClient(BaseOptions());
        var handler = StubHandler(new List<(ulong, uint)>());
        client.BindRetransmitForTesting(handler);

        client.HandleInboundEventForTesting(FakeAck(1));
        client.HandleInboundEventForTesting(FakeAck(2));
        client.HandleInboundEventForTesting(FakeAck(4));
        client.HandleInboundEventForTesting(FakeAck(5));

        var state = client.GetInboundGapStateForTesting();
        Assert.Equal(2UL, state.contiguous);
        Assert.Equal(5UL, state.highest);
        Assert.Equal(2, state.pending);
        // Missing in the [3..5] window = (5-2) - pending(2) = 1 (only seq 3).
        var missing = (uint)((state.highest - state.contiguous) - (ulong)state.pending);
        Assert.Equal(1u, missing);
    }
}
