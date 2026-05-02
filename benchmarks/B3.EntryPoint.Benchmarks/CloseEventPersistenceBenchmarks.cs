using BenchmarkDotNet.Attributes;
using B3.EntryPoint.Client.Models;
using B3.EntryPoint.Client.State;

namespace B3.EntryPoint.Benchmarks;

/// <summary>
/// Allocation profile of the close-event path on
/// <c>EntryPointClient.OnInboundEventForPersistence</c> (issue #128).
///
/// Compares the legacy v0.13.0 shape (string-keyed dict + per-event
/// <c>ulong.ToString()</c> + string-typed <see cref="OrderClosedDelta"/>) to
/// the v0.14.0 shape (typed <see cref="ClOrdID"/> dict + struct-typed delta
/// — zero per-event allocation on the close hot path).
///
/// Compile-only baseline; run via the standard BDN host
/// (<c>dotnet run -c Release --project benchmarks/B3.EntryPoint.Benchmarks
/// -- --filter *CloseEventPersistence*</c>).
/// </summary>
[MemoryDiagnoser]
public class CloseEventPersistenceBenchmarks
{
    // 1024 outstanding orders is a realistic mid-day book for a single firm.
    private const int OutstandingCount = 1024;

    private System.Collections.Concurrent.ConcurrentDictionary<string, ulong> _stringKeyed = null!;
    private System.Collections.Concurrent.ConcurrentDictionary<ClOrdID, ulong> _typedKeyed = null!;
    private ulong[] _closeIds = null!;

    [GlobalSetup]
    public void Setup()
    {
        _stringKeyed = new(StringComparer.Ordinal);
        _typedKeyed = new();
        _closeIds = new ulong[OutstandingCount];
        for (int i = 0; i < OutstandingCount; i++)
        {
            ulong id = (ulong)(i + 1);
            _closeIds[i] = id;
            _stringKeyed[id.ToString()] = (ulong)(5_900_000 + i);
            _typedKeyed[new ClOrdID(id)] = (ulong)(5_900_000 + i);
        }
    }

    /// <summary>
    /// v0.13.0 path: <c>ulong.ToString()</c> per close event, allocates twice
    /// (once for dict lookup, once for <see cref="OrderClosedDelta"/>).
    /// </summary>
    [Benchmark(Baseline = true)]
    public int Legacy_StringKeyed_PerClose()
    {
        int closed = 0;
        for (int i = 0; i < OutstandingCount; i++)
        {
            string key = _closeIds[i].ToString();
            if (_stringKeyed.TryRemove(key, out _))
            {
                _ = new LegacyClosed(key); // simulate OrderClosedDelta(string)
                closed++;
            }
        }
        return closed;
    }

    /// <summary>
    /// v0.14.0 path: typed <see cref="ClOrdID"/> dict key, no per-event
    /// string allocation. <see cref="OrderClosedDelta"/> carries the struct.
    /// </summary>
    [Benchmark]
    public int Typed_ClOrdID_PerClose()
    {
        int closed = 0;
        for (int i = 0; i < OutstandingCount; i++)
        {
            var key = new ClOrdID(_closeIds[i]);
            if (_typedKeyed.TryRemove(key, out _))
            {
                _ = new OrderClosedDelta(key);
                closed++;
            }
        }
        return closed;
    }

    private sealed record LegacyClosed(string ClOrdID);
}
