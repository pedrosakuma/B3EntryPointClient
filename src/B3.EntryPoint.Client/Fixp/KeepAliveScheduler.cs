using System.Diagnostics;

namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Default <see cref="IKeepAliveScheduler"/>. Sends a periodic <c>Sequence</c>
/// frame on the bound transport every <see cref="KeepAliveInterval"/> and
/// surfaces inbound peer <c>Sequence</c> frames through
/// <see cref="SequenceFrameReceived"/> (spec §4.6).
/// </summary>
public sealed class KeepAliveScheduler : IKeepAliveScheduler, IDisposable
{
    private readonly Func<CancellationToken, Task<ulong>>? _sendSequence;
    private readonly Action<KeepAliveTiming>? _onTiming;
    private readonly Action<KeepAliveFailure>? _onFailure;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Public constructor — produces a scheduler with no transport bound.
    /// Calling <see cref="Start"/> on such an instance throws; bind a
    /// transport via <see cref="EntryPointClient"/> instead.
    /// </summary>
    public KeepAliveScheduler(TimeSpan keepAliveInterval)
        : this(keepAliveInterval, sendSequence: null)
    { }

    internal KeepAliveScheduler(
        TimeSpan keepAliveInterval,
        Func<CancellationToken, Task<ulong>>? sendSequence,
        Action<KeepAliveTiming>? onTiming = null,
        Action<KeepAliveFailure>? onFailure = null)
    {
        if (keepAliveInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(keepAliveInterval),
                "Keep-alive interval must be positive.");
        KeepAliveInterval = keepAliveInterval;
        _sendSequence = sendSequence;
        _onTiming = onTiming;
        _onFailure = onFailure;
    }

    public TimeSpan KeepAliveInterval { get; }

    public event EventHandler<SequenceFrameEventArgs>? SequenceFrameSent;

    public event EventHandler<SequenceFrameEventArgs>? SequenceFrameReceived;

    public void Start()
    {
        if (_sendSequence is null)
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
        var expectedTick = Stopwatch.GetTimestamp() +
            (long)(KeepAliveInterval.TotalSeconds * Stopwatch.Frequency);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var sendStarted = Stopwatch.GetTimestamp();
                var schedulingDelay = sendStarted <= expectedTick
                    ? TimeSpan.Zero
                    : Stopwatch.GetElapsedTime(expectedTick, sendStarted);
                expectedTick += (long)(KeepAliveInterval.TotalSeconds * Stopwatch.Frequency);

                if (schedulingDelay >= KeepAliveInterval)
                {
                    var timeout = new TimeoutException(
                        $"Keep-alive callback started {schedulingDelay} late, exceeding the {KeepAliveInterval} liveness budget.");
                    _onTiming?.Invoke(new KeepAliveTiming(schedulingDelay, TimeSpan.Zero));
                    _onFailure?.Invoke(new KeepAliveFailure(
                        KeepAliveFailureKind.SchedulingDelay,
                        timeout,
                        schedulingDelay,
                        TimeSpan.Zero));
                    return;
                }

                var remainingBudget = KeepAliveInterval - schedulingDelay;
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(remainingBudget);
                try
                {
                    var seq = await _sendSequence!(sendCts.Token).ConfigureAwait(false);
                    var sendDuration = Stopwatch.GetElapsedTime(sendStarted);
                    _onTiming?.Invoke(new KeepAliveTiming(schedulingDelay, sendDuration));
                    RaiseFrameSent(seq, DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException ex) when (sendCts.IsCancellationRequested)
                {
                    var sendDuration = Stopwatch.GetElapsedTime(sendStarted);
                    var timeout = new TimeoutException(
                        $"Keep-alive send did not complete within the remaining {remainingBudget} liveness budget.",
                        ex);
                    _onTiming?.Invoke(new KeepAliveTiming(schedulingDelay, sendDuration));
                    _onFailure?.Invoke(new KeepAliveFailure(
                        KeepAliveFailureKind.SendTimeout,
                        timeout,
                        schedulingDelay,
                        sendDuration));
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var sendDuration = Stopwatch.GetElapsedTime(sendStarted);
                    _onTiming?.Invoke(new KeepAliveTiming(schedulingDelay, sendDuration));
                    _onFailure?.Invoke(new KeepAliveFailure(
                        KeepAliveFailureKind.SendException,
                        ex,
                        schedulingDelay,
                        sendDuration));
                    return;
                }
                catch
                {
                    // Teardown won the race with a non-cancellation transport
                    // exception. The owning session is already being stopped.
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

internal enum KeepAliveFailureKind
{
    SchedulingDelay,
    SendTimeout,
    SendException,
}

internal readonly record struct KeepAliveTiming(
    TimeSpan SchedulingDelay,
    TimeSpan SendDuration);

internal readonly record struct KeepAliveFailure(
    KeepAliveFailureKind Kind,
    Exception Exception,
    TimeSpan SchedulingDelay,
    TimeSpan SendDuration);
