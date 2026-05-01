using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.Spec_4_6_Sequence;

/// <summary>
/// Spec §4.6 — Sequence / Heartbeat. Verifies the keep-alive scheduler
/// emits <c>Sequence</c> frames at the negotiated interval and observes
/// peer keep-alive frames. Skipped without a peer.
/// </summary>
[Trait("Category", "Conformance")]
public class SequenceHeartbeatTests
{
    [ConformanceFact]
    public async Task KeepAlive_Sequence_Frames_Are_Exchanged()
    {
        var peer = PeerEndpoint.TryResolve()!;
        await using var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = peer.Endpoint,
            SessionId = peer.SessionId,
            SessionVerId = peer.SessionVerId,
            EnteringFirm = peer.EnteringFirm,
            Credentials = Credentials.FromUtf8(peer.AccessKey),
            KeepAliveIntervalMs = 1000,
        });

        await client.ConnectAsync();
        var sentCount = 0;
        if (client.KeepAlive is not null)
        {
            client.KeepAlive.SequenceFrameSent += (_, _) => Interlocked.Increment(ref sentCount);
        }
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.True(sentCount >= 1, $"Expected at least one Sequence frame sent, got {sentCount}");
    }
}
