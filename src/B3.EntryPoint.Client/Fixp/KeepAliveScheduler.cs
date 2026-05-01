namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Default <see cref="IKeepAliveScheduler"/>. API-surface stub — events and
/// <see cref="KeepAliveInterval"/> are wired, but the periodic Sequence-frame
/// send/receive loop is intentionally not implemented in this PR (issue #3).
/// </summary>
public sealed class KeepAliveScheduler : IKeepAliveScheduler
{
    public KeepAliveScheduler(TimeSpan keepAliveInterval)
    {
        if (keepAliveInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(keepAliveInterval),
                "Keep-alive interval must be positive.");
        KeepAliveInterval = keepAliveInterval;
    }

    public TimeSpan KeepAliveInterval { get; }

    public event EventHandler<SequenceFrameEventArgs>? SequenceFrameSent;

    public event EventHandler<SequenceFrameEventArgs>? SequenceFrameReceived;

    public void Start() => throw new NotImplementedException(
        "KeepAliveScheduler.Start is not yet wired to the FIXP transport. Tracked by issue #3.");

    public void Stop()
    {
        // Idempotent no-op until Start is wired.
    }

    /// <summary>
    /// Test/internal hook that lets callers surface a synthetic outbound
    /// Sequence frame through the public event. Will be replaced by the real
    /// send loop in the follow-up implementation PR.
    /// </summary>
    internal void RaiseFrameSent(ulong nextSeqNo, DateTimeOffset at) =>
        SequenceFrameSent?.Invoke(this, new SequenceFrameEventArgs(nextSeqNo, at));

    /// <summary>
    /// Test/internal hook that lets the receive loop surface inbound Sequence
    /// frames through the public event.
    /// </summary>
    internal void RaiseFrameReceived(ulong nextSeqNo, DateTimeOffset at) =>
        SequenceFrameReceived?.Invoke(this, new SequenceFrameEventArgs(nextSeqNo, at));
}

