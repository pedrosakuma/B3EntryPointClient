using System.Collections.Concurrent;
using System.Net;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.State;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Integration test for #124: <c>ReconnectAsync</c> must drain the previous
/// session's persistence worker (and the rest of the centralized teardown)
/// before sending the next <c>Establish</c>, so background work scoped to the
/// old session cannot race with the new one.
/// </summary>
public class ReconnectTeardownTests
{
    private static EntryPointClientOptions Options(InProcessFixpTestPeer peer, ISessionStateStore store) => new()
    {
        Endpoint = peer.LocalEndpoint,
        SessionId = 42u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("k"),
        KeepAliveIntervalMs = 60_000u,
        SessionStateStore = store,
        StateCompactEveryDeltas = 0,
        PersistenceQueueCapacity = 256,
        SessionTeardownTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task ReconnectAsync_DrainsPersistenceWorker_BeforeReEstablish()
    {
        await using var peer = new InProcessFixpTestPeer();
        peer.Start();

        var store = new RecordingStore();
        await using var client = new EntryPointClient(Options(peer, store));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        // Burst of close-event persistence ops on the active session's worker.
        const int burst = 50;
        for (int i = 0; i < burst; i++)
            client.EnqueuePersistOpForTesting(new B3.EntryPoint.Client.Models.ClOrdID((ulong)(i + 1)), (ulong)(i + 1));

        // Reconnect with a bumped SessionVerID. Before this returns the old
        // session's persistence worker MUST have drained every enqueued op,
        // otherwise the old worker could race against the new session's
        // persistence worker on the same store.
        await client.ReconnectAsync(2, cts.Token);

        // Snapshot what was recorded immediately on ReconnectAsync return — the
        // new session has just been Established, so any old-session op that
        // had not yet been drained would not appear here.
        var closedAtReconnect = store.Deltas.OfType<OrderClosedDelta>()
            .Select(d => d.ClOrdID)
            .ToHashSet();

        Assert.Equal(burst, closedAtReconnect.Count);
        for (int i = 0; i < burst; i++)
            Assert.Contains(new B3.EntryPoint.Client.Models.ClOrdID((ulong)(i + 1)), closedAtReconnect);
    }

    private sealed class RecordingStore : ISessionStateStore
    {
        public ConcurrentQueue<SessionDelta> Deltas { get; } = new();
        public ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default) => new((SessionSnapshot?)null);
        public ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default) => default;
        public ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default)
        {
            Deltas.Enqueue(delta);
            return default;
        }
        public ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default) => new((SessionSnapshot?)null);
        public ValueTask CompactAsync(CancellationToken ct = default) => default;
    }
}
