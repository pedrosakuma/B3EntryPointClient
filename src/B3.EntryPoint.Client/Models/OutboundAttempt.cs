namespace B3.EntryPoint.Client.Models;

/// <summary>The business operation encoded in a durable outbound attempt.</summary>
public enum OutboundOperationKind : byte
{
    NewOrder,
    Replace,
    Cancel,
}

/// <summary>The last irreversible stage reached by an outbound attempt.</summary>
public enum OutboundAttemptStage : byte
{
    NotStarted,
    SequenceReserved,
    SequenceReservedAndEncoded,
    FramePrepared,
    TransportWriteStarted,
    TransportWriteCompleted,
    SdkSessionStatePersisted,
}

/// <summary>
/// Immutable identity of one encoded outbound application frame.
/// </summary>
/// <remarks>
/// <see cref="EncodedFrameSha256"/> hashes the complete SOFH-framed SBE message.
/// Together with the session tuple and sequence number it lets consumers map
/// FIXP <c>NotApplied</c> ranges and BusinessReject <c>RefSeqNum</c> values back
/// to their own firm-scoped attempt ledger.
/// </remarks>
public sealed class OutboundFrameIdentity
{
    public OutboundFrameIdentity(
        uint sessionId,
        uint sessionVerId,
        ulong msgSeqNum,
        OutboundOperationKind operation,
        ClOrdID clOrdID,
        int encodedFrameLength,
        string encodedFrameSha256)
    {
        SessionId = sessionId;
        SessionVerId = sessionVerId;
        MsgSeqNum = msgSeqNum;
        Operation = operation;
        ClOrdID = clOrdID;
        EncodedFrameLength = encodedFrameLength;
        EncodedFrameSha256 = encodedFrameSha256;
    }

    public uint SessionId { get; }
    public uint SessionVerId { get; }
    public ulong MsgSeqNum { get; }
    public OutboundOperationKind Operation { get; }
    public ClOrdID ClOrdID { get; }
    public int EncodedFrameLength { get; }
    public string EncodedFrameSha256 { get; }
}

/// <summary>
/// Called after sequence reservation and encoding, while outbound sends remain
/// serialized, and before the first possible transport write.
/// </summary>
public delegate ValueTask OutboundFramePreparedCallback(
    OutboundFrameIdentity frame,
    CancellationToken cancellationToken);

/// <summary>Successful completion evidence for one outbound attempt.</summary>
/// <remarks>
/// This receipt proves only that the local transport write (and, when enabled,
/// flush) completed. It is not venue acceptance. Await the corresponding
/// ExecutionReport or BusinessReject for venue evidence.
/// </remarks>
public sealed class OutboundAttemptReceipt
{
    public OutboundAttemptReceipt(OutboundFrameIdentity frame, OutboundAttemptStage stage)
    {
        Frame = frame;
        Stage = stage;
    }

    public OutboundFrameIdentity Frame { get; }
    public OutboundAttemptStage Stage { get; }
}

/// <summary>Failure carrying the last outbound stage known to have completed.</summary>
/// <remarks>
/// When <see cref="NoTransportWritePossible"/> is <see langword="false"/>, a
/// partial or completed write may have occurred and the consumer must reconcile.
/// The SDK cannot safely replay an exact original frame at its original sequence
/// on the same session; consumers must reconcile rather than resend it.
/// </remarks>
public sealed class OutboundAttemptException : Exception
{
    public OutboundAttemptException(
        string message,
        OutboundAttemptStage lastStage,
        OutboundFrameIdentity? frame,
        Exception innerException)
        : base(message, innerException)
    {
        LastStage = lastStage;
        Frame = frame;
    }

    public OutboundAttemptStage LastStage { get; }
    public OutboundFrameIdentity? Frame { get; }
    public bool NoTransportWritePossible => LastStage < OutboundAttemptStage.TransportWriteStarted;
}
