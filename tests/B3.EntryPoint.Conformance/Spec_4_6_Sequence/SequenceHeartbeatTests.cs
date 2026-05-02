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
            KeepAliveIntervalMs = 250,
        });

        await client.ConnectAsync();
        Assert.NotNull(client.KeepAlive);

        var sentTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentCount = 0;
        var receivedCount = 0;
        client.KeepAlive!.SequenceFrameSent += (_, _) =>
        {
            if (Interlocked.Increment(ref sentCount) >= 1) sentTcs.TrySetResult();
        };
        client.KeepAlive.SequenceFrameReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref receivedCount) >= 1) receivedTcs.TrySetResult();
        };

        // Cap the wait at 5×interval so a stalled scheduler fails fast.
        var timeout = TimeSpan.FromMilliseconds(250 * 5);
        var both = Task.WhenAll(sentTcs.Task, receivedTcs.Task);
        var completed = await Task.WhenAny(both, Task.Delay(timeout));
        Assert.Same(both, completed);
        Assert.True(sentCount >= 1, $"Expected at least one Sequence frame sent, got {sentCount}");
        Assert.True(receivedCount >= 1, $"Expected at least one Sequence frame received, got {receivedCount}");
    }
}
