using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.State;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests.State;

/// <summary>
/// Regression tests for #152 — <c>FileSessionStateStore</c> previously only
/// wrote <c>snapshot.json</c> after <see cref="EntryPointClientOptions.StateCompactEveryDeltas"/>
/// (default 1024) appends. Restarts before that threshold lost the
/// <c>SessionId</c>/<c>SessionVerId</c>, causing the matching peer to reject
/// the next Establish with <c>InvalidSessionVerId</c>.
///
/// Fix: persist the snapshot at lifecycle boundaries — immediately after
/// Establish, and once on graceful teardown — so the on-disk identity is
/// always current regardless of delta volume.
/// </summary>
public class SnapshotDurabilityTests
{
    private static EntryPointClientOptions Options(InProcessFixpTestPeer peer, ISessionStateStore store) => new()
    {
        Endpoint = peer.LocalEndpoint,
        SessionId = 4242u,
        SessionVerId = 7u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("test-key"),
        KeepAliveIntervalMs = 60_000u,
        SessionTeardownTimeout = TimeSpan.FromSeconds(5),
        SessionStateStore = store,
        // Default of 1024 — proves the snapshot lands without ever crossing
        // the compaction threshold.
    };

    [Fact]
    public async Task Snapshot_IsPersisted_ImmediatelyAfterEstablish()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            Scenario = TestPeerScenarios.AcceptAll,
        });
        peer.Start();

        var dir = Path.Combine(Path.GetTempPath(), "b3epc-152-establish-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStateStore(dir);
            var opts = Options(peer, store);

            await using (var client = new EntryPointClient(opts))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await client.ConnectAsync(cts.Token);

                // Snapshot must already exist on disk after Establish, before
                // any orders/deltas are produced. Pre-fix this would only
                // happen after 1024 deltas.
                var snapshotPath = Path.Combine(dir, "snapshot.json");
                Assert.True(File.Exists(snapshotPath),
                    $"expected {snapshotPath} to exist immediately after Establish");

                var loaded = await store.LoadAsync(cts.Token);
                Assert.NotNull(loaded);
                Assert.Equal(opts.SessionId, loaded!.SessionId);
                Assert.Equal(opts.SessionVerId, loaded.SessionVerId);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Snapshot_SurvivesGracefulShutdown_BelowCompactionThreshold()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            Scenario = TestPeerScenarios.AcceptAll,
        });
        peer.Start();

        var dir = Path.Combine(Path.GetTempPath(), "b3epc-152-shutdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStateStore(dir);
            var opts = Options(peer, store);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using (var client = new EntryPointClient(opts))
            {
                await client.ConnectAsync(cts.Token);
                // A handful of orders — far below StateCompactEveryDeltas (1024).
                for (ulong i = 1; i <= 3; i++)
                {
                    await client.SubmitAsync(new NewOrderRequest
                    {
                        ClOrdID = (ClOrdID)i,
                        SecurityId = 1001,
                        Side = Side.Buy,
                        OrderType = OrderType.Limit,
                        Price = 10m,
                        OrderQty = 5,
                    }, cts.Token);
                }
            }
            // DisposeAsync ran StopActiveSessionAsync, which must have flushed
            // a final snapshot containing the last contiguous outbound seq.

            var loaded = await store.LoadAsync(cts.Token);
            Assert.NotNull(loaded);
            Assert.Equal(opts.SessionId, loaded!.SessionId);
            Assert.Equal(opts.SessionVerId, loaded.SessionVerId);
            Assert.True(loaded.LastOutboundSeqNum >= 3UL,
                $"expected snapshot to capture ≥3 outbound seqs, got {loaded.LastOutboundSeqNum}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
