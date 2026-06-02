namespace B3.EntryPoint.Client;

/// <summary>
/// Selects how <see cref="EntryPointClient.ConnectAsync(System.Threading.CancellationToken)"/>
/// performs the initial (cold-start / process-restart) FIXP handshake. See
/// issue #191 for the spec rationale.
/// </summary>
public enum ConnectMode
{
    /// <summary>
    /// Legacy behaviour: always perform a fresh <c>Negotiate</c> followed by
    /// <c>Establish</c>. A persisted snapshot (when configured via
    /// <see cref="EntryPointClientOptions.SessionStateStore"/>) is still used
    /// to resume the outbound/inbound sequence counters and outstanding-order
    /// set after Establish, but the wire handshake always opens a brand-new
    /// logical session under the configured <c>SessionVerID</c>.
    /// </summary>
    NegotiateThenEstablish = 0,

    /// <summary>
    /// Spec-canonical process-restart resume (B3 EntryPoint 5.3 §5.3, #191):
    /// when a usable persisted snapshot exists, reconnect TCP and send
    /// <c>Establish</c> reusing the persisted <c>SessionVerID</c> (no
    /// <c>Negotiate</c>). If the peer answers <c>EstablishmentAck</c> the
    /// session is reattached across the process restart — the venue's
    /// order-ownership and retransmit buffer survive and the
    /// <c>SessionVerID</c> is not bumped. Only when the peer rejects
    /// <c>Establish</c> with a recoverable code, or when no usable snapshot is
    /// available, does the SDK fall back to a fresh <c>Negotiate</c> (with a
    /// strictly-greater <c>SessionVerID</c> from
    /// <see cref="EntryPointClientOptions.NextSessionVerIdSelector"/> on the
    /// recoverable-reject path). Requires
    /// <see cref="EntryPointClientOptions.SessionStateStore"/> to be set.
    /// </summary>
    EstablishReuseThenNegotiate = 1,
}
