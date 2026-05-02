using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.Spec_4_7_Retransmit;

/// <summary>
/// Spec — Negative path. Peer injects an unsolicited <c>NotApplied</c>
/// frame; the client surfaces it via
/// <see cref="IRetransmitRequestHandler.NotAppliedReceived"/>.
/// </summary>
[Trait("Category", "Conformance")]
public class NotAppliedTests
{
    [TestPeerOnlyConformanceFact]
    public async Task NotApplied_From_Peer_Surfaces_Event_With_Range()
    {
        await using var fx = new ConformancePeerFactory(TestPeerScenarios.AcceptAll);
        await using var client = new EntryPointClient(fx.ClientOptions);
        await client.ConnectAsync();
        Assert.NotNull(client.Retransmit);

        var na = new TaskCompletionSource<NotAppliedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Retransmit!.NotAppliedReceived += (_, args) => na.TrySetResult(args);

        var sent = await fx.Peer.InjectNotAppliedAsync(fromSeqNo: 7u, count: 3u);
        Assert.True(sent >= 1, "Expected the NotApplied frame to be written to at least one connection");

        var completed = await Task.WhenAny(na.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(na.Task, completed);
        var evt = await na.Task;
        Assert.Equal(7UL, evt.FromSeqNo);
        Assert.Equal(3u, evt.Count);
    }
}
