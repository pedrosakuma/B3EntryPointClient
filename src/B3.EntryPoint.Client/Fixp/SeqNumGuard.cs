using B3.Entrypoint.Fixp.Sbe.V6;

namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Centralized guard for the FIXP/SBE 8.4.2 outbound <see cref="SeqNum"/> boundary.
///
/// Per <c>schemas/b3-entrypoint-messages-8.4.2.xml</c>:
/// <list type="bullet">
///   <item><c>SeqNum</c> is <c>uint32</c>, scoped to a <c>SessionID</c> + <c>SessionVerID</c> pair.</item>
///   <item><c>SessionVerID</c> "need to be incremented each time Negotiate message is sent to gateway".</item>
/// </list>
/// FIXP does not authorise silent wrap of <c>MsgSeqNum</c> back to 0/1 on the same
/// <c>SessionVerID</c>: the peer would treat the next frame as a massive backwards
/// gap and tear the session down. The correct recovery is to renegotiate with a
/// new <c>SessionVerID</c> (see <c>EntryPointClient.ReconnectAsync(uint nextSessionVerId, ...)</c>).
///
/// This helper converts the internal <c>ulong</c> counter to the wire <c>SeqNum</c>
/// and throws a clear, actionable <see cref="InvalidOperationException"/> when the
/// counter would exceed <c>uint.MaxValue</c> — instead of letting a deeply nested
/// <c>checked((uint)…)</c> cast surface as an opaque <see cref="OverflowException"/>.
/// </summary>
internal static class SeqNumGuard
{
    /// <summary>Largest value that fits in the wire <c>SeqNum</c> (uint32).</summary>
    public const ulong MaxWireSeqNum = uint.MaxValue;

    /// <summary>
    /// Threshold at which the session emits a single warning advising the operator
    /// to rotate <c>SessionVerID</c> proactively.
    /// </summary>
    public const ulong NearExhaustionThreshold = 0xFFFF_FF00UL;

    /// <summary>
    /// Converts an internal outbound counter value to a wire <see cref="SeqNum"/>.
    /// Throws <see cref="InvalidOperationException"/> when <paramref name="seqNum"/>
    /// exceeds <see cref="MaxWireSeqNum"/>; callers should rotate
    /// <c>SessionVerID</c> via <c>EntryPointClient.ReconnectAsync</c>.
    /// </summary>
    public static SeqNum ToWireSeqNum(ulong seqNum)
    {
        if (seqNum > MaxWireSeqNum)
            throw new InvalidOperationException(
                $"Outbound MsgSeqNum {seqNum} exceeds the FIXP wire SeqNum (uint32) limit " +
                $"of {MaxWireSeqNum}. Per schema v8.4.2, MsgSeqNum is scoped to a single " +
                "SessionID/SessionVerID pair and must not wrap. Rotate by calling " +
                "EntryPointClient.ReconnectAsync(nextSessionVerId, ...) with a strictly " +
                "greater SessionVerID before exhausting the counter.");
        return new SeqNum((uint)seqNum);
    }
}
