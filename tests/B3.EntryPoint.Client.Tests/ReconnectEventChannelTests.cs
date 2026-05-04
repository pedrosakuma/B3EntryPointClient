using System.Diagnostics;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Client.Tests;

/// <summary>
/// Regression test for #144 — <c>ReconnectAsync</c> must NOT close the
/// shared <c>EntryPointClient.Events</c> channel. The previous session's
/// inbound Terminate / loop-exit used to call <c>TryComplete()</c> on the
/// shared writer, leaving the new session unable to deliver any inbound
/// frames (every <c>WriteAsync</c> threw <c>ChannelClosedException</c> and
/// killed the new inbound loop).
/// </summary>
public class ReconnectEventChannelTests
{
    private static EntryPointClientOptions Options(InProcessFixpTestPeer peer) => new()
    {
        Endpoint = peer.LocalEndpoint,
        SessionId = 42u,
        SessionVerId = 1u,
        EnteringFirm = 7u,
        Credentials = Credentials.FromUtf8("test-key"),
        KeepAliveIntervalMs = 60_000u,
        SessionTeardownTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task EventsChannel_RemainsOpen_AcrossReconnect()
    {
        await using var peer = new InProcessFixpTestPeer(new TestPeerOptions
        {
            Scenario = TestPeerScenarios.AcceptAll,
        });
        peer.Start();

        await using var client = new EntryPointClient(Options(peer));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(cts.Token);

        var collected = new List<EntryPointEvent>();
        using var collectorCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var collector = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in client.Events(collectorCts.Token).ConfigureAwait(false))
                {
                    lock (collected) collected.Add(evt);
                }
            }
            catch (OperationCanceledException) { }
        });

        await client.SubmitAsync(new NewOrderRequest
        {
            ClOrdID = (ClOrdID)1UL,
            SecurityId = 1001,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = 10m,
            OrderQty = 5,
        }, cts.Token);

        await WaitForCountAsync(collected, 1, TimeSpan.FromSeconds(5));

        // Reconnect bumps SessionVerID and tears down the prior session. The
        // bug: the prior session's inbound Terminate handler completes the
        // shared event channel, so no further events can ever reach the
        // consumer.
        await client.ReconnectAsync(2u, cts.Token);

        await client.SubmitAsync(new NewOrderRequest
        {
            ClOrdID = (ClOrdID)2UL,
            SecurityId = 1001,
            Side = Side.Buy,
            OrderType = OrderType.Limit,
            Price = 10m,
            OrderQty = 5,
        }, cts.Token);

        await WaitForCountAsync(collected, 2, TimeSpan.FromSeconds(5));

        collectorCts.Cancel();
        try { await collector.ConfigureAwait(true); } catch { }

        lock (collected)
        {
            Assert.True(collected.Count >= 2,
                $"expected ≥2 events delivered across reconnect, got {collected.Count}");
        }
    }

    private static async Task WaitForCountAsync(List<EntryPointEvent> bag, int target, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int count;
            lock (bag) count = bag.Count;
            if (count >= target) return;
            if (sw.Elapsed > timeout)
                throw new TimeoutException($"timed out waiting for {target} events; got {count}");
            await Task.Delay(25).ConfigureAwait(false);
        }
    }
}
