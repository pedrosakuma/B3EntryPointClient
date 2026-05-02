using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.State;
using Xunit;

namespace B3.EntryPoint.Client.Tests.State;

public class SessionStateStoreWiringTests
{
    [Fact]
    public async Task Replay_AppliesOutboundAndClosedDeltas()
    {
        var dir = Path.Combine(Path.GetTempPath(), "b3epc-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new FileSessionStateStore(dir);
            await store.SaveAsync(new SessionSnapshot
            {
                SessionId = 42u,
                SessionVerId = 1u,
                LastOutboundSeqNum = 100UL,
                LastInboundSeqNum = 50UL,
                CapturedAt = DateTimeOffset.UtcNow,
                OutstandingOrders = new() { ["CL-1"] = 7UL, ["CL-2"] = 9UL, ["1"] = 7UL },
            });
            await store.AppendDeltaAsync(new OutboundDelta(101UL, "CL-3", 11UL));
            await store.AppendDeltaAsync(new OutboundDelta(102UL, "CL-4", 12UL));
            await store.AppendDeltaAsync(new OrderClosedDelta(new B3.EntryPoint.Client.Models.ClOrdID(1)));
            await store.AppendDeltaAsync(new InboundDelta(60UL));

            var replay = await store.ReplayAsync();
            Assert.NotNull(replay);
            Assert.Equal(102UL, replay!.LastOutboundSeqNum);
            Assert.Equal(60UL, replay.LastInboundSeqNum);
            Assert.False(replay.OutstandingOrders.ContainsKey("1"));
            Assert.True(replay.OutstandingOrders.ContainsKey("CL-3"));
            Assert.True(replay.OutstandingOrders.ContainsKey("CL-4"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EntryPointClientOptions_ExposeStateStoreSlots()
    {
        var opts = new EntryPointClientOptions
        {
            Endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1),
            SessionId = 1,
            SessionVerId = 1,
            EnteringFirm = 1,
            Credentials = Credentials.FromUtf8("k"),
        };
        Assert.Null(opts.SessionStateStore);
        Assert.True(opts.StateCompactEveryDeltas > 0);
    }

    [Fact]
    public async Task Compact_TruncatesDeltas_AfterRebuild()
    {
        var dir = Path.Combine(Path.GetTempPath(), "b3epc-compact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new FileSessionStateStore(dir);
            await store.SaveAsync(new SessionSnapshot { SessionId = 1u, SessionVerId = 1u, LastOutboundSeqNum = 10UL });
            for (ulong i = 11UL; i <= 20UL; i++)
                await store.AppendDeltaAsync(new OutboundDelta(i, $"X{i}", i));
            await store.CompactAsync();
            var replay = await store.ReplayAsync();
            Assert.Equal(20UL, replay!.LastOutboundSeqNum);
            Assert.Equal(10, replay.OutstandingOrders.Count);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

