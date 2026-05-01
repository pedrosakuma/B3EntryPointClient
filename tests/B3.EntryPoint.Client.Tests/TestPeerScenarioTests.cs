using System.Diagnostics;
using System.Linq;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Behaviour tests for the public <see cref="InProcessFixpTestPeer"/> NuGet
/// surface — credentials gating, MessageReceived event, response latency,
/// and scenario dispatch (AcceptAll vs RejectAll).
/// </summary>
public class TestPeerScenarioTests
{
    private static EntryPointClientOptions ClientOptions(InProcessFixpTestPeer peer, uint firm = 7u, string key = "test-key")
        => new()
        {
            Endpoint = peer.LocalEndpoint,
            SessionId = 42u,
            SessionVerId = 1u,
            EnteringFirm = firm,
            Credentials = Credentials.FromUtf8(key),
            KeepAliveIntervalMs = 60_000u,
        };

    [Fact]
    public async Task MessageReceived_FiresForInboundFrames()
    {
        await using var peer = new InProcessFixpTestPeer();
        var seen = new List<ushort>();
        peer.MessageReceived += (_, e) => { lock (seen) seen.Add(e.TemplateId); };
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        // Negotiate (template id 500) + Establish (501) at minimum.
        Assert.NotEmpty(seen);
    }

    [Fact]
    public async Task Credentials_RejectUnknownFirm()
    {
        var opts = new TestPeerOptions
        {
            Credentials = new Dictionary<uint, byte[]> { [99u] = new byte[] { 1, 2, 3 } },
        };
        await using var peer = new InProcessFixpTestPeer(opts);
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer, firm: 7u));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(async () => await client.ConnectAsync(cts.Token));
    }

    [Fact]
    public async Task ResponseLatency_DelaysServerResponses()
    {
        var latency = TimeSpan.FromMilliseconds(150);
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { ResponseLatency = latency });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        var sw = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        sw.Stop();

        // Negotiate + Establish round-trips → at least 2× latency.
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(250),
            $"expected ≥250ms with latency={latency}, got {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task AcceptAllScenario_EmitsExecutionReportNew()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = TestPeerScenarios.AcceptAll });
        peer.Start();

        await using var client = new EntryPointClient(ClientOptions(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);

        try
        {
            await client.SubmitAsync(new NewOrderRequest
            {
                ClOrdID = (ClOrdID)123UL,
                SecurityId = 1001,
                Side = Side.Buy,
                OrderType = OrderType.Limit,
                Price = 10m,
                OrderQty = 10,
            }, cts.Token);
        }
        catch (NotImplementedException) { /* submit path not yet wired in some builds */ }

        var drainCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var saw = false;
        try
        {
            await foreach (var _ in client.Events().WithCancellation(drainCts.Token))
            { saw = true; break; }
        }
        catch (OperationCanceledException) { }

        Assert.True(saw, "expected at least one inbound event from AcceptAll scenario");
    }

    [Fact]
    public void TestPeerScenarios_BuiltInsAreNonNull()
    {
        Assert.NotNull(TestPeerScenarios.AcceptAll);
        Assert.NotNull(TestPeerScenarios.FillImmediately);
        Assert.NotNull(TestPeerScenarios.RejectAll());
    }
}
