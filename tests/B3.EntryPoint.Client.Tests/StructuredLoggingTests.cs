using System.Linq;
using System.Net;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.TestPeer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Asserts that lifecycle events emit the documented EventIds at the
/// expected <see cref="LogLevel"/> via the source-generated
/// <c>B3.EntryPoint.Client.Logging.LogMessages</c> helpers.
/// We assert on <see cref="EventId"/> only — message text is not stable contract.
/// </summary>
public class StructuredLoggingTests
{
    private static EntryPointClientOptions Options(IPEndPoint endpoint, ILogger logger)
    {
        return new EntryPointClientOptions
        {
            Endpoint = endpoint,
            SessionId = 42u,
            SessionVerId = 1u,
            EnteringFirm = 7u,
            Credentials = Credentials.FromUtf8("test-key"),
            KeepAliveIntervalMs = 60_000u,
            Logger = logger,
        };
    }

    [Fact]
    public async Task ConnectAsync_EmitsLifecycleLogs()
    {
        await using var peer = new InProcessFixpTestPeer();
        peer.Start();
        var fake = new FakeLogger();
        var opts = Options(peer.Endpoint, fake);

        await using var client = new EntryPointClient(opts);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        // 3000 = Connected (Information)
        Assert.Contains(fake.Collector.GetSnapshot(), e => e.Id.Id == 3000 && e.Level == LogLevel.Information);

        // 2001 = Negotiated (Debug)
        Assert.Contains(fake.Collector.GetSnapshot(), e => e.Id.Id == 2001 && e.Level == LogLevel.Debug);

        // 2002 = Established (Debug)
        Assert.Contains(fake.Collector.GetSnapshot(), e => e.Id.Id == 2002 && e.Level == LogLevel.Debug);

        // State transitions (2000) emitted at least Disconnected->TcpConnected and others
        Assert.Contains(fake.Collector.GetSnapshot(), e => e.Id.Id == 2000 && e.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task InboundFrame_EmittedAtTraceLevel_WhenEnabled()
    {
        await using var peer = new InProcessFixpTestPeer();
        peer.Start();
        var fake = new FakeLogger();
        fake.ControlLevel(LogLevel.Trace, enabled: true);
        var opts = Options(peer.Endpoint, fake);

        await using var client = new EntryPointClient(opts);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        // 1001 = InboundFrame (Trace) — at minimum NegotiateResponse + EstablishAck
        var traceFrames = fake.Collector.GetSnapshot().Where(e => e.Id.Id == 1001 && e.Level == LogLevel.Trace).ToList();
        Assert.True(traceFrames.Count >= 2, $"expected ≥2 inbound trace frames, got {traceFrames.Count}");
    }

    [Fact]
    public async Task ConnectAsync_Exhausted_LogsErrorEventId5000()
    {
        // Closed port → connect must fail.
        var fake = new FakeLogger();
        var opts = Options(new IPEndPoint(IPAddress.Loopback, 1), fake);
        opts.ConnectMaxAttempts = 1;
        opts.ConnectTimeout = TimeSpan.FromMilliseconds(500);

        await using var client = new EntryPointClient(opts);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(cts.Token));

        Assert.Contains(fake.Collector.GetSnapshot(), e => e.Id.Id == 5000 && e.Level == LogLevel.Error);
    }
}
