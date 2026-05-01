using B3.EntryPoint.Client.State;

namespace B3.EntryPoint.Client.Tests.State;

public class FileSessionStateStoreTests
{
    [Fact]
    public async Task Snapshot_RoundTrip_PreservesFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "b3-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStateStore(dir);
            var snap = new SessionSnapshot
            {
                SessionId = 42,
                SessionVerId = 7,
                LastOutboundSeqNum = 100,
                LastInboundSeqNum = 90,
                CapturedAt = DateTimeOffset.UnixEpoch,
                OutstandingOrders = new() { ["A"] = 1, ["B"] = 2 },
            };
            await store.SaveAsync(snap);
            var loaded = await store.LoadAsync();
            Assert.NotNull(loaded);
            Assert.Equal(42u, loaded!.SessionId);
            Assert.Equal(7u, loaded.SessionVerId);
            Assert.Equal(100UL, loaded.LastOutboundSeqNum);
            Assert.Equal(2, loaded.OutstandingOrders.Count);
            Assert.Equal(1UL, loaded.OutstandingOrders["A"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task DeltaReplay_AppliesOutbound_Inbound_AndOrderClosed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "b3-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStateStore(dir);
            await store.SaveAsync(new SessionSnapshot
            {
                SessionId = 1,
                SessionVerId = 1,
                LastOutboundSeqNum = 10,
                LastInboundSeqNum = 5,
                OutstandingOrders = new() { ["X"] = 100 },
            });
            await store.AppendDeltaAsync(new OutboundDelta(11, "Y", 200));
            await store.AppendDeltaAsync(new OutboundDelta(12, "Z", 300));
            await store.AppendDeltaAsync(new InboundDelta(7));
            await store.AppendDeltaAsync(new OrderClosedDelta("X"));

            var rebuilt = await store.ReplayAsync();
            Assert.NotNull(rebuilt);
            Assert.Equal(12UL, rebuilt!.LastOutboundSeqNum);
            Assert.Equal(7UL, rebuilt.LastInboundSeqNum);
            Assert.False(rebuilt.OutstandingOrders.ContainsKey("X"));
            Assert.Equal(200UL, rebuilt.OutstandingOrders["Y"]);
            Assert.Equal(300UL, rebuilt.OutstandingOrders["Z"]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Compact_DropsDeltaLog_And_PersistsRebuilt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "b3-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStateStore(dir);
            await store.SaveAsync(new SessionSnapshot { SessionId = 1, SessionVerId = 1 });
            await store.AppendDeltaAsync(new OutboundDelta(1, "A", 99));
            Assert.True(File.Exists(Path.Combine(dir, "deltas.jsonl")));
            await store.CompactAsync();
            Assert.False(File.Exists(Path.Combine(dir, "deltas.jsonl")));
            var snap = await store.LoadAsync();
            Assert.Equal(1UL, snap!.LastOutboundSeqNum);
            Assert.Single(snap.OutstandingOrders);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Load_ReturnsNull_WhenStoreEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "b3-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSessionStateStore(dir);
            Assert.Null(await store.LoadAsync());
            Assert.Null(await store.ReplayAsync());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
