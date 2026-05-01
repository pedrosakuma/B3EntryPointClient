namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Default <see cref="IKeepAliveScheduler"/>. Sends a periodic <c>Sequence</c>
/// frame on the bound transport every <see cref="KeepAliveInterval"/> and
/// surfaces inbound peer <c>Sequence</c> frames through
/// <see cref="SequenceFrameReceived"/> (spec §4.6).
/// </summary>
public sealed class KeepAliveScheduler : IKeepAliveScheduler, IDisposable
{
    private readonly Func<ulong, CancellationToken, Task>? _sendSequence;
    private readonly Func<ulong>? _nextSeqNo;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Public constructor — produces a scheduler with no transport bound.
    /// Calling <see cref="Start"/> on such an instance throws; bind a
    /// transport via <see cref="EntryPointClient"/> instead.
    /// </summary>
    public KeepAliveScheduler(TimeSpan keepAliveInterval)
        : this(keepAliveInterval, sendSequence: null, nextSeqNo: null)
    { }

    internal KeepAliveScheduler(
        TimeSpan keepAliveInterval,
        Func<ulong, CancellationToken, Task>? sendSequence,
        Func<ulong>? nextSeqNo)
    {
        if (keepAliveInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(keepAliveInterval),
                "Keep-alive interval must be positive.");
        KeepAliveInterval = keepAliveInterval;
        _sendSequence = sendSequence;
        _nextSeqNo = nextSeqNo;
    }

    public TimeSpan KeepAliveInterval { get; }

    public event EventHandler<SequenceFrameEventArgs>? SequenceFrameSent;

    public event EventHandler<SequenceFrameEventArgs>? SequenceFrameReceived;

    public void Start()
    {
        if (_sendSequence is null || _nextSeqNo is null)
            throw new InvalidOperationException(
                "KeepAliveScheduler was constructed without a bound transport. " +
                "Use EntryPointClient.ConnectAsync, which wires a scheduler internally.");
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(KeepAliveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var seq = _nextSeqNo!();
                try
                {
                    await _sendSequence!(seq, ct).ConfigureAwait(false);
                    RaiseFrameSent(seq, DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // Send failed (peer closed, IO error). Stop quietly; the
                    // session-level error handling surfaces the disconnect.
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    /// <summary>Internal hook used by tests and by the inbound dispatcher.</summary>
    internal void RaiseFrameSent(ulong nextSeqNo, DateTimeOffset at) =>
        SequenceFrameSent?.Invoke(this, new SequenceFrameEventArgs(nextSeqNo, at));

    /// <summary>Internal hook used by the inbound dispatcher when a Sequence frame arrives.</summary>
    internal void RaiseFrameReceived(ulong nextSeqNo, DateTimeOffset at) =>
        SequenceFrameReceived?.Invoke(this, new SequenceFrameEventArgs(nextSeqNo, at));
}

