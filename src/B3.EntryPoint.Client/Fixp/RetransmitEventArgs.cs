namespace B3.EntryPoint.Client.Fixp;

/// <summary>Reason a peer rejected a <c>RetransmitRequest</c> (schema enum <c>RetransmitRejectCode</c>).</summary>
public enum RetransmitRejectCode : byte
{
    OutOfRange = 0,
    InvalidSession = 1,
    RequestLimitExceeded = 2,
    RetransmitInProgress = 3,
    InvalidTimestamp = 4,
    InvalidFromSeqNo = 5,
    InvalidCount = 9,
    ThrottleReject = 10,
    SystemBusy = 11,
}

/// <summary>Payload of <see cref="IRetransmitRequestHandler.RetransmitRequested"/>.</summary>
public sealed class RetransmitRequestedEventArgs : EventArgs
{
    public RetransmitRequestedEventArgs(ulong fromSeqNo, uint count, DateTimeOffset at)
    {
        FromSeqNo = fromSeqNo;
        Count = count;
        At = at;
    }
    public ulong FromSeqNo { get; }
    public uint Count { get; }
    public DateTimeOffset At { get; }
}

/// <summary>Payload of <see cref="IRetransmitRequestHandler.RetransmissionReceived"/>.</summary>
public sealed class RetransmissionEventArgs : EventArgs
{
    public RetransmissionEventArgs(ulong nextSeqNo, uint count, DateTimeOffset requestTimestamp)
    {
        NextSeqNo = nextSeqNo;
        Count = count;
        RequestTimestamp = requestTimestamp;
    }
    public ulong NextSeqNo { get; }
    public uint Count { get; }
    public DateTimeOffset RequestTimestamp { get; }
}

/// <summary>Payload of <see cref="IRetransmitRequestHandler.RetransmitRejected"/>.</summary>
public sealed class RetransmitRejectedEventArgs : EventArgs
{
    public RetransmitRejectedEventArgs(RetransmitRejectCode code, DateTimeOffset requestTimestamp)
    {
        Code = code;
        RequestTimestamp = requestTimestamp;
    }
    public RetransmitRejectCode Code { get; }
    public DateTimeOffset RequestTimestamp { get; }
}

/// <summary>
/// Payload of <see cref="IRetransmitRequestHandler.NotAppliedReceived"/> —
/// the peer reports a range of inbound messages it discarded for idempotence
/// or validation reasons (schema <c>NotApplied</c>).
/// </summary>
public sealed class NotAppliedEventArgs : EventArgs
{
    public NotAppliedEventArgs(ulong fromSeqNo, uint count)
    {
        FromSeqNo = fromSeqNo;
        Count = count;
    }
    public ulong FromSeqNo { get; }
    public uint Count { get; }
}
