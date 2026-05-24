using System.Linq;
using System.Net;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Unit tests for the bounded retransmission window introduced in #175.
/// FIXP §4.7: an inbound <c>Retransmission(NextSeqNo, Count)</c> envelope
/// promises the peer will deliver exactly <c>Count</c> ordered app frames
/// with seqs <c>NextSeqNo, NextSeqNo+1, ..., NextSeqNo+Count-1</c>. The
/// client tracks the bounded reply so it can detect protocol violations
/// (under-/over-delivery, overlapping replies, out-of-sequence seqs)
/// without silently absorbing them.
/// </summary>
public class RetransmissionWindowUnitTests
{
    private static EntryPointClientOptions BaseOptions(ILogger? logger = null)
    {
        var opts = new EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
            SessionId = 42u,
            SessionVerId = 1u,
            EnteringFirm = 7u,
            Credentials = Credentials.FromUtf8("k"),
        };
        if (logger is not null)
            opts.Logger = logger;
        return opts;
    }

    private static bool HasEvent(FakeLogger fake, int id) =>
        fake.Collector.GetSnapshot().Any(e => e.Id.Id == id);

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
    public void Window_OpensOnNonZeroCount_ClosesAfterExactlyCountOrderedFrames()
    {
        var fake = new FakeLogger();
        var client = new EntryPointClient(BaseOptions(fake));
        client.SetInboundGapStateForTesting(contiguous: 2UL, highest: 5UL);
        client.OpenRetransmissionWindowForTesting(nextSeqNo: 3UL, count: 3u);

        Assert.Equal((3UL, 3u, 0u, false), client.GetRetransmissionWindowForTesting());
        client.HandleInboundEventForTesting(FakeAck(3));
        Assert.Equal((3UL, 3u, 1u, false), client.GetRetransmissionWindowForTesting());
        client.HandleInboundEventForTesting(FakeAck(4));
        Assert.Equal((3UL, 3u, 2u, false), client.GetRetransmissionWindowForTesting());
        // Reaching Count flips Completed=true but the window stays installed
        // so an extra in-range frame can be classified as over-delivery.
        client.HandleInboundEventForTesting(FakeAck(5));
        Assert.Equal((3UL, 3u, 3u, true), client.GetRetransmissionWindowForTesting());
        // 4016 = RetransmissionWindowCompleted (Information)
        Assert.True(HasEvent(fake, 4016));
        // First out-of-range frame after completion is normal live traffic
        // and closes the window silently.
        client.HandleInboundEventForTesting(FakeAck(6));
        Assert.Null(client.GetRetransmissionWindowForTesting());
        // No under-/over-delivery on a clean completion + live continuation.
        Assert.False(HasEvent(fake, 4019));
        Assert.False(HasEvent(fake, 4020));
    }

    [Fact]
    public void Window_NotOpenedWhenCountIsZero()
    {
        var client = new EntryPointClient(BaseOptions());
        client.OpenRetransmissionWindowForTesting(nextSeqNo: 10UL, count: 0u);
        Assert.Null(client.GetRetransmissionWindowForTesting());
    }

    [Fact]
    public void Window_DuplicateSeqInsideRange_DoesNotAdvanceCounter()
    {
        var fake = new FakeLogger();
        var client = new EntryPointClient(BaseOptions(fake));
        client.SetInboundGapStateForTesting(contiguous: 2UL, highest: 5UL);
        client.OpenRetransmissionWindowForTesting(nextSeqNo: 3UL, count: 3u);

        client.HandleInboundEventForTesting(FakeAck(3));
        Assert.Equal((3UL, 3u, 1u, false), client.GetRetransmissionWindowForTesting());

        // Duplicate of last delivered seq → out-of-sequence, no advance.
        client.HandleInboundEventForTesting(FakeAck(3));
        Assert.Equal((3UL, 3u, 1u, false), client.GetRetransmissionWindowForTesting());

        // Skip-ahead inside range (expected 4, got 5) → out-of-sequence.
        client.HandleInboundEventForTesting(FakeAck(5));
        Assert.Equal((3UL, 3u, 1u, false), client.GetRetransmissionWindowForTesting());
        // 4018 = RetransmissionFrameOutOfSequence
        Assert.True(HasEvent(fake, 4018));
    }

    [Fact]
    public void Window_OutOfWindowSeqBeforeCompletion_TerminatesAsUnderDelivered()
    {
        var fake = new FakeLogger();
        var client = new EntryPointClient(BaseOptions(fake));
        client.SetInboundGapStateForTesting(contiguous: 2UL, highest: 5UL);
        client.OpenRetransmissionWindowForTesting(nextSeqNo: 3UL, count: 3u);

        client.HandleInboundEventForTesting(FakeAck(3));
        client.HandleInboundEventForTesting(FakeAck(4));
        Assert.Equal((3UL, 3u, 2u, false), client.GetRetransmissionWindowForTesting());

        // Peer skips seq 5 and jumps to live seq 99 — burst terminated
        // early (Count-1 frames). Window closes with under-delivery.
        client.HandleInboundEventForTesting(FakeAck(99));
        Assert.Null(client.GetRetransmissionWindowForTesting());
        // 4019 = RetransmissionWindowUnderDelivered
        Assert.True(HasEvent(fake, 4019));
    }

    [Fact]
    public void Window_OverDelivery_DetectedWhenExtraInRangeFrameArrivesAfterCompletion()
    {
        var fake = new FakeLogger();
        var client = new EntryPointClient(BaseOptions(fake));
        client.SetInboundGapStateForTesting(contiguous: 2UL, highest: 5UL);
        client.OpenRetransmissionWindowForTesting(nextSeqNo: 3UL, count: 2u);

        client.HandleInboundEventForTesting(FakeAck(3));
        client.HandleInboundEventForTesting(FakeAck(4));
        // Count reached but window stays installed in Completed state.
        Assert.Equal((3UL, 2u, 2u, true), client.GetRetransmissionWindowForTesting());

        // Peer re-sends a seq still inside [3,4] — genuine over-delivery
        // (extra Retransmission frame beyond declared Count). Window stays
        // open until the next out-of-range frame closes it.
        client.HandleInboundEventForTesting(FakeAck(3));
        Assert.Equal((3UL, 2u, 2u, true), client.GetRetransmissionWindowForTesting());
        // 4020 = RetransmissionWindowOverDelivered
        Assert.True(HasEvent(fake, 4020));

        // Next-live frame at NextSeqNo+Count closes the window cleanly.
        client.HandleInboundEventForTesting(FakeAck(5));
        Assert.Null(client.GetRetransmissionWindowForTesting());
    }

    [Fact]
    public void Window_OverlappedByNewReply_ReplacesAndResetsReceived()
    {
        var fake = new FakeLogger();
        var client = new EntryPointClient(BaseOptions(fake));
        client.SetInboundGapStateForTesting(contiguous: 2UL, highest: 5UL);

        client.OpenRetransmissionWindowForTesting(nextSeqNo: 3UL, count: 5u);
        client.HandleInboundEventForTesting(FakeAck(3));
        Assert.Equal((3UL, 5u, 1u, false), client.GetRetransmissionWindowForTesting());

        // Peer opens a NEW bounded reply before the previous one completes
        // (protocol violation). Previous window is replaced; counters reset.
        // Production-path mirrored: logs BOTH 4019 (under-delivery of prior)
        // AND 4017 (overlap).
        client.OpenRetransmissionWindowForTesting(nextSeqNo: 10UL, count: 2u);
        Assert.Equal((10UL, 2u, 0u, false), client.GetRetransmissionWindowForTesting());
        Assert.True(HasEvent(fake, 4019));
        Assert.True(HasEvent(fake, 4017));

        client.HandleInboundEventForTesting(FakeAck(10));
        client.HandleInboundEventForTesting(FakeAck(11));
        Assert.Equal((10UL, 2u, 2u, true), client.GetRetransmissionWindowForTesting());
    }

    [Fact]
    public void Window_PartialDelivery_StaysOpenUntilOutOfRangeFrame()
    {
        var client = new EntryPointClient(BaseOptions());
        client.SetInboundGapStateForTesting(contiguous: 2UL, highest: 5UL);
        client.OpenRetransmissionWindowForTesting(nextSeqNo: 3UL, count: 3u);

        client.HandleInboundEventForTesting(FakeAck(3));
        client.HandleInboundEventForTesting(FakeAck(4));
        Assert.Equal((3UL, 3u, 2u, false), client.GetRetransmissionWindowForTesting());
        // No out-of-range frame yet — window still open, awaiting seq 5.
        Assert.NotNull(client.GetRetransmissionWindowForTesting());
    }
}
