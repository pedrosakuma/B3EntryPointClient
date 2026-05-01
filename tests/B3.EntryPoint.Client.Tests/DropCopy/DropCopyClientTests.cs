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
    public async Task ConnectAsync_Throws_With_Issue_Citation()
    {
        await using var c = new DropCopyClient(Opts(SessionProfile.DropCopy));
        var ex = await Assert.ThrowsAsync<NotImplementedException>(() => c.ConnectAsync());
        Assert.Contains("issue #10", ex.Message);
    }
}
