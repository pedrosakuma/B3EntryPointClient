using System.Collections.Concurrent;
using System.Net;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace B3.EntryPoint.Client.Tests.State;

/// <summary>
/// Behavioural tests for the persistence worker introduced for #121
/// (replacement for the legacy <c>Task.Run</c> in
/// <c>EntryPointClient.OnInboundEventForPersistence</c>) and the centralized
/// teardown introduced for #124 (<c>StopActiveSessionAsync</c>).
///
/// These exercise the worker via the <c>internal</c> test hooks so they do not
/// require a live FIXP session.
/// </summary>
public class PersistenceWorkerTests
{
    private static EntryPointClientOptions BaseOptions(ISessionStateStore store, ILogger? logger = null) => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
        SessionId = 42u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("k"),
        SessionStateStore = store,
        StateCompactEveryDeltas = 0,
        Logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
    };

    [Fact]
    public async Task DisposeAfterBurst_DrainsPersistenceWorker_NoDeltasLost()
    {
        var store = new RecordingStore();
        var opts = BaseOptions(store);
        opts.PersistenceQueueCapacity = 64;
        opts.SessionTeardownTimeout = TimeSpan.FromSeconds(5);

        var client = new EntryPointClient(opts);
        client.StartPersistenceWorkerForTesting();

        const int burst = 200;
        for (int i = 0; i < burst; i++)
            client.EnqueuePersistOpForTesting($"clord-{i}", (ulong)(i + 1));

        await client.StopActiveSessionForTestingAsync();

        // Every enqueued op should produce one OrderClosedDelta + one InboundDelta.
        var closed = store.Deltas.OfType<OrderClosedDelta>().Select(d => d.ClOrdID).ToHashSet();
        var inbound = store.Deltas.OfType<InboundDelta>().Select(d => d.SeqNum).ToHashSet();
        Assert.Equal(burst, closed.Count);
        Assert.Equal(burst, inbound.Count);
        for (int i = 0; i < burst; i++)
        {
            Assert.Contains($"clord-{i}", closed);
            Assert.Contains((ulong)(i + 1), inbound);
        }
    }

    [Fact]
    public async Task TransientStoreFailure_IsLoggedOnce_AndWorkerContinues()
    {
        var fake = new FakeLogger();
        var store = new FlakyStore(failFirstClOrdID: "clord-1");
        var opts = BaseOptions(store, fake);
        opts.PersistenceQueueCapacity = 16;

        var client = new EntryPointClient(opts);
        client.StartPersistenceWorkerForTesting();

        client.EnqueuePersistOpForTesting("clord-0", 1);
        client.EnqueuePersistOpForTesting("clord-1", 2); // throws transiently
        client.EnqueuePersistOpForTesting("clord-2", 3);
        client.EnqueuePersistOpForTesting("clord-3", 4);

        await client.StopActiveSessionForTestingAsync();

        // The failing op should be logged via OrderClosedPersistFailed (EventId 4005).
        var failures = fake.Collector.GetSnapshot().Where(e => e.Id.Id == 4005).ToList();
        Assert.Single(failures);

        // Subsequent ops still recorded — worker did not die.
        var ids = store.Deltas.OfType<OrderClosedDelta>().Select(d => d.ClOrdID).ToHashSet();
        Assert.Contains("clord-0", ids);
        Assert.Contains("clord-2", ids);
        Assert.Contains("clord-3", ids);
        // clord-1 OrderClosedDelta was attempted but threw before AppendDeltaAsync recorded — not recorded.
        Assert.DoesNotContain("clord-1", ids);
    }

    [Fact]
    public async Task TeardownTimeout_IsEmitted_WhenStoreHangs()
    {
        var fake = new FakeLogger();
        var hang = new TaskCompletionSource();
        var store = new HangingStore(hang.Task);
        var opts = BaseOptions(store, fake);
        opts.PersistenceQueueCapacity = 8;
        opts.SessionTeardownTimeout = TimeSpan.FromMilliseconds(150);

        var client = new EntryPointClient(opts);
        client.StartPersistenceWorkerForTesting();
        client.EnqueuePersistOpForTesting("hang-1", 1);

        // Give the worker a moment to pick it up and block inside AppendDeltaAsync.
        await Task.Delay(50);

        await client.StopActiveSessionForTestingAsync();

        // EventId 4009 is SessionTeardownTimeout.
        Assert.Contains(fake.Collector.GetSnapshot(), e => e.Id.Id == 4009 && e.Level == LogLevel.Warning);

        // Release the hanging store so the worker task is not leaked across tests.
        hang.TrySetResult();
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

    private sealed class FlakyStore : ISessionStateStore
    {
        private readonly string _failFirstClOrdID;
        private bool _hasFailed;
        public ConcurrentQueue<SessionDelta> Deltas { get; } = new();
        public FlakyStore(string failFirstClOrdID) { _failFirstClOrdID = failFirstClOrdID; }
        public ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default) => new((SessionSnapshot?)null);
        public ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default) => default;
        public ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default)
        {
            if (!_hasFailed && delta is OrderClosedDelta closed && closed.ClOrdID == _failFirstClOrdID)
            {
                _hasFailed = true;
                throw new InvalidOperationException("transient store failure");
            }
            Deltas.Enqueue(delta);
            return default;
        }
        public ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default) => new((SessionSnapshot?)null);
        public ValueTask CompactAsync(CancellationToken ct = default) => default;
    }

    private sealed class HangingStore : ISessionStateStore
    {
        private readonly Task _release;
        public HangingStore(Task release) { _release = release; }
        public ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default) => new((SessionSnapshot?)null);
        public ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default) => default;
        public async ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default)
        {
            await _release.WaitAsync(ct).ConfigureAwait(false);
        }
        public ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default) => new((SessionSnapshot?)null);
        public ValueTask CompactAsync(CancellationToken ct = default) => default;
    }
}
