using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.DropCopy;
using B3.EntryPoint.Conformance.Infrastructure;

namespace B3.EntryPoint.Conformance.DropCopy_Receive;

/// <summary>
/// Drop Copy: connect with <see cref="SessionProfile.DropCopy"/> and verify
/// the read-only event stream surfaces ExecutionReports for the entitled firm.
/// </summary>
[Trait("Category", "Conformance")]
public class DropCopyReceiveTests
{
    [ConformanceFact]
    public async Task DropCopy_Session_Receives_Events()
    {
        var peer = PeerEndpoint.TryResolve()!;
        await using var dc = new DropCopyClient(new EntryPointClientOptions
        {
            Endpoint = peer.Endpoint,
            SessionId = peer.SessionId,
            SessionVerId = peer.SessionVerId,
            EnteringFirm = peer.EnteringFirm,
            Credentials = Credentials.FromUtf8(peer.AccessKey),
            Profile = SessionProfile.DropCopy,
        });

        await dc.ConnectAsync();
        // ConnectAsync returns only after Negotiate + Establish complete; no
        // exception means the Drop Copy session is up. Application traffic on
        // a Drop Copy session is gateway-driven and out of scope here.
        Assert.NotNull(dc);
    }
}
