namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Payload of <see cref="IKeepAliveScheduler.SequenceFrameSent"/> and
/// <see cref="IKeepAliveScheduler.SequenceFrameReceived"/>. Mirrors the FIXP
/// <c>Sequence</c> message (spec §4.6) — the next expected sequence number
/// reported by the local or remote endpoint at <see cref="At"/>.
/// </summary>
public sealed class SequenceFrameEventArgs : EventArgs
{
    public SequenceFrameEventArgs(ulong nextSeqNo, DateTimeOffset at)
    {
        NextSeqNo = nextSeqNo;
        At = at;
    }

    public ulong NextSeqNo { get; }

    public DateTimeOffset At { get; }
}
