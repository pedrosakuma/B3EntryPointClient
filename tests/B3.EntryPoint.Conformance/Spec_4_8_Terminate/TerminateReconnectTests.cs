using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.Spec_4_8_Terminate;

/// <summary>
/// Spec §4.8 — Terminate / Reconnect / CancelOnDisconnect. Verifies a
/// clean Terminate handshake and that re-establishing requires a strictly
/// greater <c>SessionVerID</c>.
/// </summary>
[Trait("Category", "Conformance")]
public class TerminateReconnectTests
{
    [ConformanceFact]
    public async Task Terminate_Then_Reconnect_With_Next_SessionVerId()
    {
        var peer = PeerEndpoint.TryResolve()!;
        await using var client = new EntryPointClient(new EntryPointClientOptions
        {
            Endpoint = peer.Endpoint,
            SessionId = peer.SessionId,
            SessionVerId = peer.SessionVerId,
            EnteringFirm = peer.EnteringFirm,
            Credentials = Credentials.FromUtf8(peer.AccessKey),
            CancelOnDisconnect = CancelOnDisconnectType.CancelOnDisconnectOrTerminate,
        });

        await client.ConnectAsync();
        await client.TerminateAsync(TerminationCode.Finished);
        await client.ReconnectAsync(peer.SessionVerId + 1);
    }
}
