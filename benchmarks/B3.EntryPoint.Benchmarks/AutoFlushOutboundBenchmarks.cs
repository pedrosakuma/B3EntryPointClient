using BenchmarkDotNet.Attributes;
using B3.EntryPoint.Client;
using B3.EntryPoint.Client.Auth;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.TestPeer;

namespace B3.EntryPoint.Benchmarks;

/// <summary>
/// Compares per-frame transport flushing (default,
/// <see cref="EntryPointClientOptions.AutoFlushOutboundFrames"/> = <c>true</c>)
/// vs. batched submission with a single explicit
/// <see cref="EntryPointClient.FlushAsync"/> at the end (#123). The transport
/// is the in-process <see cref="InProcessFixpTestPeer"/>; this isolates the
/// flush-vs-batch cost from real network/TLS effects but still exercises the
/// real wire encoder, FIXP framing, and outbound serialization paths.
/// </summary>
/// <remarks>
/// Run from the repo root:
/// <code>dotnet run -c Release --project benchmarks/B3.EntryPoint.Benchmarks -- --filter *AutoFlush*</code>
/// </remarks>
[MemoryDiagnoser]
public class AutoFlushOutboundBenchmarks
{
    [Params(64, 1024)]
    public int OrderCount;

    private InProcessFixpTestPeer _peerAutoFlush = null!;
    private InProcessFixpTestPeer _peerBatched = null!;
    private EntryPointClient _clientAutoFlush = null!;
    private EntryPointClient _clientBatched = null!;

    [GlobalSetup]
    public void Setup()
    {
        (_peerAutoFlush, _clientAutoFlush) = StartClient(autoFlush: true);
        (_peerBatched, _clientBatched) = StartClient(autoFlush: false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _clientAutoFlush.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _clientBatched.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _peerAutoFlush.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _peerBatched.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task SubmitN_AutoFlushTrue()
    {
        for (int i = 0; i < OrderCount; i++)
            await _clientAutoFlush.SubmitAsync(NewOrder((ulong)i + 1UL), CancellationToken.None);
    }

    [Benchmark]
    public async Task SubmitN_AutoFlushFalse_FlushAtEnd()
    {
        for (int i = 0; i < OrderCount; i++)
            await _clientBatched.SubmitAsync(NewOrder((ulong)i + 1UL), CancellationToken.None);
        await _clientBatched.FlushAsync();
    }

    private static (InProcessFixpTestPeer peer, EntryPointClient client) StartClient(bool autoFlush)
    {
        var peer = new InProcessFixpTestPeer(new TestPeerOptions { Scenario = TestPeerScenarios.AcceptAll });
        peer.Start();
        var opts = new EntryPointClientOptions
        {
            Endpoint = peer.LocalEndpoint,
            SessionId = 42u,
            SessionVerId = 1u,
            EnteringFirm = 7u,
            Credentials = Credentials.FromUtf8("bench-key"),
            KeepAliveIntervalMs = 60_000u,
            AutoFlushOutboundFrames = autoFlush,
        };
        var client = new EntryPointClient(opts);
        client.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (peer, client);
    }

    private static NewOrderRequest NewOrder(ulong clOrdId) => new()
    {
        ClOrdID = (ClOrdID)clOrdId,
        SecurityId = 1001UL,
        Side = Side.Buy,
        OrderType = OrderType.Limit,
        Price = 10m,
        OrderQty = 1UL,
    };
}
