using System.Collections.Concurrent;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Conformance.Infrastructure;
using NewOrderSingleData = B3.Entrypoint.Fixp.Sbe.V6.NewOrderSingleData;

namespace B3.EntryPoint.Conformance.Spec_4_7_Retransmit;

/// <summary>
/// Spec §4.7 — In-session inbound gap detection (issue #138). Asserts that the
/// client auto-emits a <c>RetransmitRequest</c> when a peer-side outbound app
/// frame is dropped, and that the <c>InboundGapAtReconnect</c> event does
/// <i>not</i> fire after a clean Terminate (the event is reserved for the
/// reconnect path).
/// </summary>
[Trait("Category", "Conformance")]
public class InboundGapDetectionTests
{
    [TestPeerOnlyConformanceFact]
    public async Task InboundGapTriggersRetransmitRequest()
    {
        // Drop the 2nd outbound app frame so the client receives ER seqs
        // [1, 3]. The arrival of seq 3 should trigger the gap-detection logic
        // to emit RetransmitRequest(fromSeqNo=2, count=1).
        var schedule = new Dictionary<int, OutboundFrameAction>
        {
            [2] = new OutboundFrameAction.Drop(),
        };
        var scenario = TestPeerScenarios.WithSequenceFaults(TestPeerScenarios.AcceptAll, schedule);
        await using var fx = new ConformancePeerFactory(scenario);

        var peerInboundNosSeqs = new ConcurrentQueue<uint>();
        fx.Peer.MessageReceived += (_, args) =>
        {
            if (args.TemplateId != NewOrderSingleData.MESSAGE_ID) return;
            if (!NewOrderSingleData.TryParse(args.Payload.Span, out var reader)) return;
            peerInboundNosSeqs.Enqueue(reader.Data.BusinessHeader.MsgSeqNum.Value);
        };

        await using var client = new EntryPointClient(fx.ClientOptions);
        await client.ConnectAsync();

        var retransmitRequested = new TaskCompletionSource<RetransmitRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Retransmit!.RetransmitRequested += (_, args) => retransmitRequested.TrySetResult(args);

        var inboundGapAtReconnectFired = false;
        client.InboundGapAtReconnect += (_, _) => inboundGapAtReconnectFired = true;

        for (ulong i = 1; i <= 3; i++)
        {
            await client.SubmitAsync(new NewOrderRequest
            {
                ClOrdID = (ClOrdID)i,
                SecurityId = 4321UL,
                Side = Side.Buy,
                OrderType = OrderType.Limit,
                Price = 10.0m,
                OrderQty = 100UL,
            });
        }

        // Drain the 2 ERs we expect to actually arrive (seq 2 was dropped).
        await DrainEventsAsync(client, expected: 2, TimeSpan.FromSeconds(5));

        // The client should have detected the gap (received seq=3 while
        // contiguous tail was 1) and issued RetransmitRequest(2, 1).
        var rr = await retransmitRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2UL, rr.FromSeqNo);
        Assert.Equal(1u, rr.Count);

        // Clean Terminate. InboundGapAtReconnect must NOT fire — that event
        // is reserved for the reconnect path. (Terminate alone does not
        // surface unrecovered gaps; the consumer simply loses the session.)
        await client.TerminateAsync(TerminationCode.Finished);

        // Settle window: give any spurious event a chance to surface.
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(inboundGapAtReconnectFired, "InboundGapAtReconnect must not fire on clean Terminate.");
    }

    [TestPeerOnlyConformanceFact]
    public async Task InboundGapAtReconnect_FiresWhenUnrecoveredGapAtReconnect()
    {
        // Drop the 2nd outbound app frame (ER seq 2). The peer's TestPeer
        // implementation answers RetransmitRequest with an empty
        // Retransmission frame (count=0), so the gap stays unrecovered.
        // After ReconnectAsync bumps SessionVerID, the client must surface
        // the leftover gap via InboundGapAtReconnect.
        var schedule = new Dictionary<int, OutboundFrameAction>
        {
            [2] = new OutboundFrameAction.Drop(),
        };
        var scenario = TestPeerScenarios.WithSequenceFaults(TestPeerScenarios.AcceptAll, schedule);
        await using var fx = new ConformancePeerFactory(scenario);

        var options = fx.ClientOptions;
        await using var client = new EntryPointClient(options);
        await client.ConnectAsync();

        var gapAtReconnect = new TaskCompletionSource<InboundGapAtReconnectEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.InboundGapAtReconnect += (_, args) => gapAtReconnect.TrySetResult(args);

        var priorVer = options.SessionVerId;

        for (ulong i = 1; i <= 3; i++)
        {
            await client.SubmitAsync(new NewOrderRequest
            {
                ClOrdID = (ClOrdID)i,
                SecurityId = 4321UL,
                Side = Side.Buy,
                OrderType = OrderType.Limit,
                Price = 10.0m,
                OrderQty = 100UL,
            });
        }

        // Drain the 2 ERs we expect (seq 2 dropped).
        await DrainEventsAsync(client, expected: 2, TimeSpan.FromSeconds(5));

        // ReconnectAsync — the prior session's gap (seq 2) is unrecovered;
        // event must fire exactly once after the new session establishes.
        await client.ReconnectAsync(options.SessionVerId + 1);

        var args = await gapAtReconnect.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2UL, args.FromSeqNo);
        Assert.Equal(1u, args.Count);
        Assert.Equal((ulong)priorVer, args.PriorSessionVerId);
    }

    private static async Task<List<ulong>> DrainEventsAsync(EntryPointClient client, int expected, TimeSpan timeout)
    {
        var seqs = new List<ulong>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var evt in client.Events().WithCancellation(cts.Token))
            {
                seqs.Add(evt.SeqNum);
                if (seqs.Count >= expected) break;
            }
        }
        catch (OperationCanceledException) { /* timeout — caller may inspect */ }
        return seqs;
    }
}
