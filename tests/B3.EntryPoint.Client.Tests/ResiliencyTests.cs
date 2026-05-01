using System.Net;
using System.Net.Sockets;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;

namespace B3.EntryPoint.Client.Tests;

public class ResiliencyTests
{
    [Fact]
    public async Task ConnectAsync_RetriesUpToConfiguredAttempts_WhenTcpFails()
    {
        var opts = new EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 9), // discard port — no listener
            SessionId = 1,
            SessionVerId = 1,
            EnteringFirm = 1,
            Credentials = Credentials.FromUtf8("k"),
            ConnectMaxAttempts = 3,
            ConnectBaseDelay = TimeSpan.FromMilliseconds(5),
            ConnectMaxDelay = TimeSpan.FromMilliseconds(20),
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
        };
        await using var c = new EntryPointClient(opts);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<SocketException>(() => c.ConnectAsync());
        sw.Stop();

        // 2 backoffs occurred between 3 attempts; lower bound ≈ 2 * baseDelay.
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(8),
            $"Expected at least one backoff delay; elapsed={sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Options_DefaultRetryAndIdle_AreFailFastSafe()
    {
        var o = new EntryPointClientOptions
        {
            Endpoint = new IPEndPoint(IPAddress.Loopback, 1),
            SessionId = 1,
            SessionVerId = 1,
            EnteringFirm = 1,
            Credentials = Credentials.FromUtf8("k"),
        };
        Assert.Equal(1, o.ConnectMaxAttempts);
        Assert.Equal(TimeSpan.Zero, o.IdleTimeout);
        Assert.NotNull(o.Logger);
    }
}
