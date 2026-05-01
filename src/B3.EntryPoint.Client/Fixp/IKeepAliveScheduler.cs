namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Drives the FIXP keep-alive loop (spec §4.6 — <c>Sequence</c> message). The
/// scheduler sends an outbound <c>Sequence</c> frame every
/// <see cref="KeepAliveInterval"/>, surfaces inbound peer <c>Sequence</c>
/// frames, and is the natural place to detect peer keep-alive timeouts and
/// hand off to the §4.7 retransmit handler when a gap is detected.
/// </summary>
/// <remarks>
/// API surface only — the wire-level send/receive loop is wired in a
/// follow-up PR. See issue #3.
/// </remarks>
public interface IKeepAliveScheduler
{
    /// <summary>Interval used for outbound <c>Sequence</c> frames.</summary>
    TimeSpan KeepAliveInterval { get; }

    /// <summary>Raised after an outbound <c>Sequence</c> frame is sent.</summary>
    event EventHandler<SequenceFrameEventArgs>? SequenceFrameSent;

    /// <summary>Raised when a peer <c>Sequence</c> frame is received.</summary>
    event EventHandler<SequenceFrameEventArgs>? SequenceFrameReceived;

    /// <summary>Start the keep-alive loop.</summary>
    void Start();

    /// <summary>Stop the keep-alive loop. Idempotent.</summary>
    void Stop();
}
