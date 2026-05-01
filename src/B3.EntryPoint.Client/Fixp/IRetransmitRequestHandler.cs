namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Recovers missed messages via the FIXP <c>RetransmitRequest</c>/
/// <c>Retransmission</c>/<c>RetransmitReject</c> trio (spec §4.7) and surfaces
/// <c>NotApplied</c> notifications from the peer. The §4.6 keep-alive scheduler
/// drives gap detection and calls <see cref="RequestRetransmitAsync"/>.
/// </summary>
/// <remarks>
/// API surface only — wire-level send/receive lands in a follow-up PR (issue #5).
/// </remarks>
public interface IRetransmitRequestHandler
{
    event EventHandler<RetransmitRequestedEventArgs>? RetransmitRequested;
    event EventHandler<RetransmissionEventArgs>? RetransmissionReceived;
    event EventHandler<RetransmitRejectedEventArgs>? RetransmitRejected;
    event EventHandler<NotAppliedEventArgs>? NotAppliedReceived;

    /// <summary>
    /// Issue a <c>RetransmitRequest</c> for <paramref name="count"/> messages
    /// starting at <paramref name="fromSeqNo"/>. Range is 1..1000 per spec.
    /// </summary>
    Task RequestRetransmitAsync(ulong fromSeqNo, uint count, CancellationToken ct = default);
}
