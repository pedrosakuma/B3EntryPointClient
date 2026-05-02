using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.TestPeer;
using B3.EntryPoint.Conformance.Infrastructure;
using SbeRetransmitRejectCode = B3.Entrypoint.Fixp.Sbe.V6.RetransmitRejectCode;

namespace B3.EntryPoint.Conformance.Spec_4_7_Retransmit;

/// <summary>
/// Spec §4.7 — Negative path. Peer responds to <c>RetransmitRequest</c>
/// with <c>RetransmitReject</c>; the client surfaces a
/// <see cref="IRetransmitRequestHandler.RetransmitRejected"/> event with
/// the rejection code.
/// </summary>
[Trait("Category", "Conformance")]
public class RetransmitRejectTests
{
    [TestPeerOnlyConformanceFact]
    public async Task Retransmit_Reject_Is_Surfaced_With_Code()
    {
        await using var fx = new ConformancePeerFactory(TestPeerScenarios.AcceptAll);
        fx.Peer.Options.RetransmitRejectCode = SbeRetransmitRejectCode.OUT_OF_RANGE;

        await using var client = new EntryPointClient(fx.ClientOptions);
        await client.ConnectAsync();
        Assert.NotNull(client.Retransmit);

        var rejected = new TaskCompletionSource<RetransmitRejectedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Retransmit!.RetransmitRejected += (_, args) => rejected.TrySetResult(args);

        await client.Retransmit.RequestRetransmitAsync(fromSeqNo: 1UL, count: 5U);

        var completed = await Task.WhenAny(rejected.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(rejected.Task, completed);
        var evt = await rejected.Task;
        Assert.Equal(RetransmitRejectCode.OutOfRange, evt.Code);
    }
}
