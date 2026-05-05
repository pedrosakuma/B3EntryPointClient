using System.Net;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.DropCopy;

namespace B3.EntryPoint.Client.Tests.DropCopy;

public class DropCopyClientTests
{
    private static EntryPointClientOptions Opts(SessionProfile profile) => new()
    {
        Endpoint = new IPEndPoint(IPAddress.Loopback, 9999),
        SessionId = 1,
        SessionVerId = 1,
        EnteringFirm = 1,
        Credentials = Credentials.FromUtf8("k"),
        Profile = profile,
    };

    [Fact]
    public void Ctor_Rejects_NonDropCopy_Profile()
    {
        var ex = Assert.Throws<ArgumentException>(() => new DropCopyClient(Opts(SessionProfile.OrderEntry)));
        Assert.Contains("DropCopy", ex.Message);
    }

    [Fact]
    public async Task ConnectAsync_AttemptsTcpConnect()
    {
        await using var c = new DropCopyClient(Opts(SessionProfile.DropCopy));
        // Confirms the delegating wire-up to EntryPointClient.ConnectAsync is
        // in place. We deliberately do not assert on a specific exception
        // type: depending on what (if anything) is listening on the unused
        // port, the post-validation failure may surface as SocketException
        // (connection refused), EndOfStreamException (a stray peer that
        // closes immediately), or another transport exception. The profile
        // guard, in contrast, throws ArgumentException synchronously in the
        // constructor — so observing anything else here proves we got past
        // construction and into the connect path. (#157)
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => c.ConnectAsync());
        Assert.IsNotType<ArgumentException>(ex);
        Assert.IsNotType<InvalidOperationException>(ex);
    }
}
