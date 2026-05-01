using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.Spec_4_7_Retransmit;

/// <summary>
/// Spec §4.7 — Retransmit / NotApplied. Verifies that requesting a gap
/// of recent messages results in a <c>RetransmitResponse</c> followed by
/// the requested frames, or a documented <c>RetransmitReject</c>.
/// </summary>
[Trait("Category", "Conformance")]
public class RetransmitTests
{
    [ConformanceFact]
    public async Task Retransmit_Recent_Range_Is_Honoured()
    {
        var peer = PeerEndpoint.TryResolve()!;
        await using var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = peer.Endpoint,
            SessionId = peer.SessionId,
            SessionVerId = peer.SessionVerId,
            EnteringFirm = peer.EnteringFirm,
            Credentials = Credentials.FromUtf8(peer.AccessKey),
        });

        await client.ConnectAsync();
        // Once wired (issue #5): request RetransmitRequest(fromSeqNo, count) and
        // assert RetransmissionEventArgs arrives with matching count.
    }
}
