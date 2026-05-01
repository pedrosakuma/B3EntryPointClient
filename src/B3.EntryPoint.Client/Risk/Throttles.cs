using System.Collections.Concurrent;

namespace B3.EntryPoint.Client.Risk;

/// <summary>
/// Sliding-window throttle that limits outbound order entry requests per
/// <c>ClOrdID</c> prefix. A request is throttled when more than
/// <see cref="MaxPerWindow"/> have been observed within
/// <see cref="WindowDuration"/> for the same prefix.
/// </summary>
public sealed class ClOrdIdPrefixThrottle : IPreTradeGate
{
    private readonly int _prefixLength;
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    public int MaxPerWindow { get; }
    public TimeSpan WindowDuration { get; }

    public ClOrdIdPrefixThrottle(int prefixLength, int maxPerWindow, TimeSpan windowDuration)
    {
        if (prefixLength <= 0) throw new ArgumentOutOfRangeException(nameof(prefixLength));
        if (maxPerWindow <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerWindow));
        if (windowDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(windowDuration));
        _prefixLength = prefixLength;
        MaxPerWindow = maxPerWindow;
        WindowDuration = windowDuration;
    }

    public ValueTask<RiskDecision> EvaluateAsync(OutboundRequest request, CancellationToken ct)
    {
        var clordid = request.ClOrdID ?? string.Empty;
        var prefix = clordid.Length >= _prefixLength
            ? clordid.Substring(0, _prefixLength)
            : clordid;
        var now = DateTime.UtcNow;
        var window = _windows.GetOrAdd(prefix, _ => new Window());
        lock (window)
        {
            if (now - window.Start > WindowDuration)
            {
                window.Start = now;
                window.Count = 0;
            }
            window.Count++;
            if (window.Count > MaxPerWindow)
                return ValueTask.FromResult(RiskDecision.Throttle(
                    $"ClOrdID prefix '{prefix}' exceeded {MaxPerWindow} requests / {WindowDuration.TotalMilliseconds}ms"));
        }
        return ValueTask.FromResult(RiskDecision.Allow());
    }

    private sealed class Window { public DateTime Start; public int Count; }
}

/// <summary>
/// Sliding-window throttle that limits outbound order entry requests per
/// <c>SecurityID</c>. Same semantics as <see cref="ClOrdIdPrefixThrottle"/>.
/// </summary>
public sealed class SecurityIdRateThrottle : IPreTradeGate
{
    private readonly ConcurrentDictionary<ulong, Window> _windows = new();
    public int MaxPerWindow { get; }
    public TimeSpan WindowDuration { get; }

    public SecurityIdRateThrottle(int maxPerWindow, TimeSpan windowDuration)
    {
        if (maxPerWindow <= 0) throw new ArgumentOutOfRangeException(nameof(maxPerWindow));
        if (windowDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(windowDuration));
        MaxPerWindow = maxPerWindow;
        WindowDuration = windowDuration;
    }

    public ValueTask<RiskDecision> EvaluateAsync(OutboundRequest request, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var window = _windows.GetOrAdd(request.SecurityId, _ => new Window());
        lock (window)
        {
            if (now - window.Start > WindowDuration)
            {
                window.Start = now;
                window.Count = 0;
            }
            window.Count++;
            if (window.Count > MaxPerWindow)
                return ValueTask.FromResult(RiskDecision.Throttle(
                    $"SecurityId={request.SecurityId} exceeded {MaxPerWindow} requests / {WindowDuration.TotalMilliseconds}ms"));
        }
        return ValueTask.FromResult(RiskDecision.Allow());
    }

    private sealed class Window { public DateTime Start; public int Count; }
}
