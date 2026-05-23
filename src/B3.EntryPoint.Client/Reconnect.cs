namespace B3.EntryPoint.Client;

/// <summary>
/// Selects how <see cref="EntryPointClient.ReconnectAsync(ReconnectMode, System.Func{uint, uint}?, System.Threading.CancellationToken)"/>
/// re-attaches a dropped session. See issue #173 for the spec rationale.
/// </summary>
public enum ReconnectMode
{
    /// <summary>
    /// Spec-canonical TCP-blip recovery (B3 EntryPoint 5.3 §5.5–§5.6):
    /// reconnect TCP and send <c>Establish</c> reusing the same
    /// <c>SessionVerID</c>. If the peer answers <c>EstablishmentAck</c> the
    /// session is reattached and the application never observes a
    /// <c>SessionVerID</c> bump. Only when the peer rejects <c>Establish</c>
    /// with a recoverable code (<c>UNNEGOTIATED</c>, <c>SESSION_BLOCKED</c>,
    /// <c>INVALID_SESSIONID</c>, <c>INVALID_SESSIONVERID</c>,
    /// <c>ALREADY_ESTABLISHED</c>, <c>ESTABLISH_ATTEMPTS_EXCEEDED</c>) does
    /// the SDK fall back to a fresh <c>Negotiate</c> with a strictly-greater
    /// <c>SessionVerID</c> supplied by the caller's selector.
    /// </summary>
    EstablishReuseThenNegotiate = 0,

    /// <summary>
    /// Always send a fresh <c>Negotiate</c> with a strictly-greater
    /// <c>SessionVerID</c>. Equivalent to the legacy
    /// <see cref="EntryPointClient.ReconnectAsync(uint, System.Threading.CancellationToken)"/>
    /// overload; useful for operator-initiated rotation, daily session roll,
    /// or any case where a deliberate new logical session is wanted.
    /// </summary>
    AlwaysNegotiate = 1,
}

/// <summary>
/// Outcome of a <see cref="ReconnectMode.EstablishReuseThenNegotiate"/> /
/// <see cref="ReconnectMode.AlwaysNegotiate"/> reconnect (#173).
/// </summary>
public enum ReconnectKind
{
    /// <summary>
    /// The peer accepted <c>Establish</c> reusing the previous
    /// <c>SessionVerID</c>: the application-layer session is the same one,
    /// outstanding orders remain correlated to their original
    /// <c>ClOrdID</c>s, and the <c>SessionVerID</c> was not bumped.
    /// </summary>
    Reattached = 0,

    /// <summary>
    /// A fresh <c>Negotiate</c> was performed and the <c>SessionVerID</c> was
    /// bumped via the caller-supplied selector. The application layer should
    /// treat this as a brand-new logical session (any pre-reconnect order
    /// state is no longer addressable by the peer).
    /// </summary>
    Renegotiated = 1,
}

/// <summary>
/// Result of <see cref="EntryPointClient.ReconnectAsync(ReconnectMode, System.Func{uint, uint}?, System.Threading.CancellationToken)"/>.
/// </summary>
/// <param name="Kind">
/// Whether the underlying session was reattached (same <c>SessionVerID</c>)
/// or renegotiated (strictly-greater <c>SessionVerID</c>).
/// </param>
/// <param name="SessionVerId">
/// The <c>SessionVerID</c> in effect after the reconnect. Equal to the
/// pre-reconnect value when <paramref name="Kind"/> is
/// <see cref="ReconnectKind.Reattached"/>; the new (selector-chosen) value
/// when <see cref="ReconnectKind.Renegotiated"/>.
/// </param>
/// <param name="ServerNextSeqNoExpected">
/// <c>NextSeqNo</c> reported by the peer's <c>EstablishmentAck</c> — the next
/// inbound application sequence number the server intends to send. Always
/// <c>0</c> on the <see cref="ReconnectKind.Renegotiated"/> path because the
/// peer's outbound counter is reset to 1 across a new session (no
/// retransmission window applies).
/// </param>
/// <param name="ServerLastIncomingSeqNoSeen">
/// <c>LastIncomingSeqNo</c> reported by the peer's <c>EstablishmentAck</c> —
/// the last outbound application sequence number the server confirms having
/// received. Always <c>0</c> on the <see cref="ReconnectKind.Renegotiated"/>
/// path.
/// </param>
/// <param name="RetransmitWindowReady">
/// <c>true</c> when the <c>EstablishmentAck</c> indicates a gap in either
/// direction relative to the client's local counters — i.e. when issuing a
/// §4.7 <c>RetransmitRequest</c> (inbound gap) or re-sending outbound frames
/// past <paramref name="ServerLastIncomingSeqNoSeen"/> is appropriate.
/// Always <c>false</c> on the <see cref="ReconnectKind.Renegotiated"/> path.
/// </param>
public readonly record struct ReconnectOutcome(
    ReconnectKind Kind,
    uint SessionVerId,
    ulong ServerNextSeqNoExpected,
    ulong ServerLastIncomingSeqNoSeen,
    bool RetransmitWindowReady);
