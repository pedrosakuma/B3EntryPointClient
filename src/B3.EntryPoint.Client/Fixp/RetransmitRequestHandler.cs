namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Default <see cref="IRetransmitRequestHandler"/>. Sends FIXP §4.7 RetransmitRequest
/// frames and surfaces inbound Retransmission/RetransmitReject/NotApplied notifications.
/// </summary>
public sealed class RetransmitRequestHandler : IRetransmitRequestHandler
{
    /// <summary>Maximum messages per <c>RetransmitRequest</c> (spec §4.7).</summary>
    public const uint MaxCountPerRequest = 1000;

    private readonly Func<ulong, uint, CancellationToken, Task>? _sendRequest;

    /// <summary>
    /// Public constructor — produces an unbound handler.
    /// <see cref="RequestRetransmitAsync"/> on such an instance throws; bind via
    /// <see cref="EntryPointClient"/> instead.
    /// </summary>
    public RetransmitRequestHandler() : this(sendRequest: null) { }

    internal RetransmitRequestHandler(Func<ulong, uint, CancellationToken, Task>? sendRequest)
    {
        _sendRequest = sendRequest;
    }

    public event EventHandler<RetransmitRequestedEventArgs>? RetransmitRequested;
    public event EventHandler<RetransmissionEventArgs>? RetransmissionReceived;
    public event EventHandler<RetransmitRejectedEventArgs>? RetransmitRejected;
    public event EventHandler<NotAppliedEventArgs>? NotAppliedReceived;

    public async Task RequestRetransmitAsync(ulong fromSeqNo, uint count, CancellationToken ct = default)
    {
        if (count is 0 or > MaxCountPerRequest)
            throw new ArgumentOutOfRangeException(nameof(count),
                $"count must be in [1, {MaxCountPerRequest}].");
        if (fromSeqNo == 0)
            throw new ArgumentOutOfRangeException(nameof(fromSeqNo), "fromSeqNo must be > 0.");
        if (_sendRequest is null)
            throw new InvalidOperationException(
                "RetransmitRequestHandler was constructed without a bound transport. " +
                "Use EntryPointClient.ConnectAsync, which wires a handler internally.");
        await _sendRequest(fromSeqNo, count, ct).ConfigureAwait(false);
        RaiseRetransmitRequested(fromSeqNo, count, DateTimeOffset.UtcNow);
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

