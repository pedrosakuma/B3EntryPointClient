namespace B3.EntryPoint.Client;

/// <summary>
/// Payload of <see cref="EntryPointClient.InboundGapAtReconnect"/>. Surfaces an
/// inbound application-frame gap that was outstanding when the prior session
/// terminated and is therefore unrecoverable in-band — the peer bumps
/// <c>SessionVerID</c> on reconnect and resets its outbound counter to 1, so
/// the missing range from the prior session cannot be served by a §4.7
/// <c>RetransmitRequest</c> against the new session. Consumers should reconcile
/// out-of-band (e.g. via a business-layer order-status query). Emitted exactly
/// once per <see cref="EntryPointClient.ReconnectAsync"/> call. (#138)
/// </summary>
public sealed class InboundGapAtReconnectEventArgs : EventArgs
{
    public InboundGapAtReconnectEventArgs(ulong fromSeqNo, uint count, ulong priorSessionVerId)
    {
        FromSeqNo = fromSeqNo;
        Count = count;
        PriorSessionVerId = priorSessionVerId;
    }

    /// <summary>First missing inbound sequence number from the prior session.</summary>
    public ulong FromSeqNo { get; }

    /// <summary>Number of consecutive missing inbound sequence numbers starting at <see cref="FromSeqNo"/>.</summary>
    public uint Count { get; }

    /// <summary><c>SessionVerID</c> of the prior session in which the gap occurred.</summary>
    public ulong PriorSessionVerId { get; }
}
