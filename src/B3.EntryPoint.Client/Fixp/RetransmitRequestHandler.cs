namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Default <see cref="IRetransmitRequestHandler"/>. API-surface stub — events
/// are wired and <see cref="RequestRetransmitAsync"/> validates the range, but
/// the wire-level send loop is intentionally not implemented in this PR
/// (issue #5).
/// </summary>
public sealed class RetransmitRequestHandler : IRetransmitRequestHandler
{
    /// <summary>Maximum messages per <c>RetransmitRequest</c> (spec §4.7).</summary>
    public const uint MaxCountPerRequest = 1000;

    public event EventHandler<RetransmitRequestedEventArgs>? RetransmitRequested;
    public event EventHandler<RetransmissionEventArgs>? RetransmissionReceived;
    public event EventHandler<RetransmitRejectedEventArgs>? RetransmitRejected;
    public event EventHandler<NotAppliedEventArgs>? NotAppliedReceived;

    public Task RequestRetransmitAsync(ulong fromSeqNo, uint count, CancellationToken ct = default)
    {
        if (count is 0 or > MaxCountPerRequest)
            throw new ArgumentOutOfRangeException(nameof(count),
                $"count must be in [1, {MaxCountPerRequest}].");
        if (fromSeqNo == 0)
            throw new ArgumentOutOfRangeException(nameof(fromSeqNo), "fromSeqNo must be > 0.");
        throw new NotImplementedException(
            "RequestRetransmitAsync is not yet wired to the FIXP transport. Tracked by issue #5.");
    }

    internal void RaiseRetransmitRequested(ulong fromSeqNo, uint count, DateTimeOffset at) =>
        RetransmitRequested?.Invoke(this, new RetransmitRequestedEventArgs(fromSeqNo, count, at));

    internal void RaiseRetransmissionReceived(ulong nextSeqNo, uint count, DateTimeOffset reqTs) =>
        RetransmissionReceived?.Invoke(this, new RetransmissionEventArgs(nextSeqNo, count, reqTs));

    internal void RaiseRetransmitRejected(RetransmitRejectCode code, DateTimeOffset reqTs) =>
        RetransmitRejected?.Invoke(this, new RetransmitRejectedEventArgs(code, reqTs));

    internal void RaiseNotAppliedReceived(ulong fromSeqNo, uint count) =>
        NotAppliedReceived?.Invoke(this, new NotAppliedEventArgs(fromSeqNo, count));
}

