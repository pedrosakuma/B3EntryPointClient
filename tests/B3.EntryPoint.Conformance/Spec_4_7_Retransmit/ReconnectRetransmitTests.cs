using System.Collections.Concurrent;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.State;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Conformance.Infrastructure;
using NewOrderSingleData = B3.Entrypoint.Fixp.Sbe.V6.NewOrderSingleData;

namespace B3.EntryPoint.Conformance.Spec_4_7_Retransmit;

/// <summary>
/// Spec §4.7 + §4.8 — End-to-end conformance for the
/// "send → drop → terminate → reconnect → resume" flow with a
/// <see cref="ISessionStateStore"/> backing the warm-restart counters
/// (issue #125).
///
/// <para>
/// Wires together three previously-tested-in-isolation behaviours:
/// peer-side outbound app-frame drop (§4.7 / #113 <c>OnOutboundFrame</c>
/// hook), persistence of <c>OutboundDelta</c>s + last-inbound seq, and
/// <c>ReconnectAsync</c> (§4.8) hydrating a fresh session from the
/// snapshot. The point is to prove the wires are crossed correctly when
/// the v0.11.1 <c>LastAssignedOutboundSeqNum</c> snapshot fix has to
/// survive a terminate.
/// </para>
///
/// <para>
/// <b>Pending production gap:</b> this test does <i>not</i> assert that
/// the client auto-emits a <c>RetransmitRequest</c> on resume to recover
/// the dropped peer-outbound seq. The current client tracks
/// <c>_lastInboundSeqNum</c> as a running max — a gap (e.g. inbound seqs
/// arriving as <c>1, 2, 4, 5</c>) silently advances the counter to <c>5</c>
/// without ever surfacing a §4.7 <c>RetransmitRequest</c>, and
/// <see cref="EntryPointClient.ReconnectAsync"/> bumps SessionVerID, which
/// resets the peer's outbound counter to 1, making a same-session retransmit
/// out of band anyway. Filed as a follow-up issue (see test note below);
/// the assertion is intentionally commented out so this test stays a
/// regression for the parts that <i>do</i> work today rather than turning
/// red on an undocumented production behaviour change.
/// </para>
/// </summary>
[Trait("Category", "Conformance")]
public class ReconnectRetransmitTests
{
    [TestPeerOnlyConformanceFact]
    public async Task Reconnect_With_Persisted_State_Resumes_Outbound_SeqNum_After_Drop()
    {
        // 1. Peer scenario: drop the 3rd outbound app frame (i.e. the 3rd
        //    ExecutionReport_New) so the client receives ER seqs [1,2,4,5].
        var schedule = new Dictionary<int, OutboundFrameAction>
        {
            [3] = new OutboundFrameAction.Drop(),
        };
        var scenario = TestPeerScenarios.WithSequenceFaults(TestPeerScenarios.AcceptAll, schedule);

        await using var fx = new ConformancePeerFactory(scenario);

        // Capture the MsgSeqNum the peer assigns to every NewOrderSingle it
        // reads from the client. Used to prove the client's outbound counter
        // resumes contiguously across the reconnect.
        var peerInboundNosSeqs = new ConcurrentQueue<uint>();
        fx.Peer.MessageReceived += (_, args) =>
        {
            if (args.TemplateId != NewOrderSingleData.MESSAGE_ID) return;
            if (!NewOrderSingleData.TryParse(args.Payload.Span, out var reader)) return;
            peerInboundNosSeqs.Enqueue(reader.Data.BusinessHeader.MsgSeqNum.Value);
        };

        var store = new InMemorySessionStateStore();

        var options = fx.ClientOptions;
        options.SessionStateStore = store;
        options.StateCompactEveryDeltas = 1;
        options.PersistenceQueueCapacity = 256;
        options.SessionTeardownTimeout = TimeSpan.FromSeconds(5);

        await using var client = new EntryPointClient(options);
        await client.ConnectAsync();

        // #138 — the client now auto-detects inbound gaps and emits a §4.7
        // RetransmitRequest. Register the listener BEFORE submitting orders
        // so we don't race the asynchronous request issued from the inbound
        // loop the moment the post-gap frame (ER seq 4) arrives.
        var retransmitRequested = new TaskCompletionSource<RetransmitRequestedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Retransmit!.RetransmitRequested += (_, args) => retransmitRequested.TrySetResult(args);

        // 2. Submit 5 NewOrderSingle requests sequentially.
        for (ulong i = 1; i <= 5; i++)
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

        // 3. Drain inbound events until we've received the 4 ERs the peer
        //    actually puts on the wire (ER seq 3 is dropped). TCS-based wait
        //    via Events()'s IAsyncEnumerable + a hard timeout — no wall-clock
        //    polling.
        var clientInboundSeqs = await DrainEventsAsync(client, expected: 4, TimeSpan.FromSeconds(5));

        // 4. Peer received NewOrderSingle MsgSeqNums [1,2,3,4,5] (client→peer
        //    direction is uninterrupted). Client received ER seqs [1,2,4,5]
        //    (peer→client #3 dropped by the scenario).
        Assert.Equal(new uint[] { 1, 2, 3, 4, 5 }, peerInboundNosSeqs.OrderBy(s => s).ToArray());
        Assert.Equal(new ulong[] { 1, 2, 4, 5 }, clientInboundSeqs.OrderBy(s => s).ToArray());

        // 5. Clean terminate from the client. Peer closes its connection on
        //    inbound Terminate. Persistence worker drains in StopActiveSession
        //    (#121 / #124) before the snapshot is rebuilt.
        await client.TerminateAsync(TerminationCode.Finished);

        // Snapshot the persisted state via Replay to prove the deltas survive.
        var replayed = await store.ReplayAsync();
        Assert.NotNull(replayed);
        Assert.Equal(5UL, replayed!.LastOutboundSeqNum);

        // 6. Reconnect with bumped SessionVerID. Reconnect flows through
        //    HydrateFromSnapshotAsync, which calls ResumeOutboundSeqNum(6) —
        //    this is the v0.11.1 LastAssignedOutboundSeqNum fix exercised
        //    end-to-end across a terminate boundary.
        peerInboundNosSeqs.Clear();
        await client.ReconnectAsync(options.SessionVerId + 1);
        Assert.Equal(FixpClientState.Established, client.State);

        // 7. Submit one more order; assert the peer sees it on the wire with
        //    MsgSeqNum = 6, proving the outbound counter resumed contiguously.
        await client.SubmitAsync(new NewOrderRequest
        {
            ClOrdID = (ClOrdID)6,
            SecurityId = 4321UL,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = 10.0m,
            OrderQty = 100UL,
        });

        // Wait briefly for the peer to read the frame off the wire (TCS via
        // a one-shot signal on the MessageReceived event).
        var seenSix = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<TestPeerMessageEventArgs> probe = (_, args) =>
        {
            if (args.TemplateId != NewOrderSingleData.MESSAGE_ID) return;
            if (!NewOrderSingleData.TryParse(args.Payload.Span, out var reader)) return;
            seenSix.TrySetResult(reader.Data.BusinessHeader.MsgSeqNum.Value);
        };
        // Drain anything already enqueued from the resubmitted order.
        if (peerInboundNosSeqs.TryPeek(out var first))
        {
            seenSix.TrySetResult(first);
        }
        else
        {
            fx.Peer.MessageReceived += probe;
            try
            {
                var completed = await Task.WhenAny(seenSix.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.Same(seenSix.Task, completed);
            }
            finally
            {
                fx.Peer.MessageReceived -= probe;
            }
        }
        Assert.Equal(6u, await seenSix.Task);

        // 8. #138 — assert the auto-emitted RetransmitRequest from step 2's
        //    gap detection (ER seq 3 was dropped). Listener was wired up
        //    front so we don't race the inbound loop's async send.
        var rr = await retransmitRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3UL, rr.FromSeqNo);
        Assert.Equal(1u, rr.Count);
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
        catch (OperationCanceledException) { /* timeout — caller asserts on count */ }
        return seqs;
    }

    /// <summary>
    /// Minimal in-memory <see cref="ISessionStateStore"/> for conformance
    /// tests. Mirrors the <c>FileSessionStateStore</c> Replay semantics
    /// (snapshot + appended deltas → rebuilt snapshot) without touching
    /// disk, so the warm-restart path can be exercised hermetically.
    /// </summary>
    private sealed class InMemorySessionStateStore : ISessionStateStore
    {
        private readonly object _gate = new();
        private SessionSnapshot? _snapshot;
        private readonly List<SessionDelta> _deltas = new();

        public ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default)
        {
            lock (_gate) return new(_snapshot);
        }

        public ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            lock (_gate)
            {
                _snapshot = snapshot;
                _deltas.Clear();
            }
            return default;
        }

        public ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(delta);
            lock (_gate) _deltas.Add(delta);
            return default;
        }

        public ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default)
        {
            SessionSnapshot? snap;
            SessionDelta[] deltas;
            lock (_gate)
            {
                snap = _snapshot;
                deltas = _deltas.ToArray();
            }
            if (snap is null && deltas.Length == 0) return new((SessionSnapshot?)null);
            snap ??= new SessionSnapshot { CapturedAt = DateTimeOffset.UtcNow };
            var outstanding = new Dictionary<string, ulong>(snap.OutstandingOrders);
            ulong outboundSeq = snap.LastOutboundSeqNum;
            ulong inboundSeq = snap.LastInboundSeqNum;
            foreach (var d in deltas)
            {
                switch (d)
                {
                    case OutboundDelta o:
                        outboundSeq = Math.Max(outboundSeq, o.SeqNum);
                        outstanding[o.ClOrdID] = o.SecurityId;
                        break;
                    case InboundDelta i:
                        inboundSeq = Math.Max(inboundSeq, i.SeqNum);
                        break;
                    case OrderClosedDelta c:
                        outstanding.Remove(c.ClOrdID);
                        break;
                }
            }
            return new(snap with
            {
                LastOutboundSeqNum = outboundSeq,
                LastInboundSeqNum = inboundSeq,
                OutstandingOrders = outstanding,
                CapturedAt = DateTimeOffset.UtcNow,
            });
        }

        public async ValueTask CompactAsync(CancellationToken ct = default)
        {
            var rebuilt = await ReplayAsync(ct).ConfigureAwait(false);
            if (rebuilt is null) return;
            await SaveAsync(rebuilt, ct).ConfigureAwait(false);
        }
    }
}
