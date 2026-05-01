namespace B3.EntryPoint.Client;

/// <summary>
/// Reason carried in a FIXP <c>Terminate</c> frame (schema enum
/// <c>TerminationCode</c>). Both client and peer use these codes.
/// </summary>
public enum TerminationCode : byte
{
    Unspecified = 0,
    Finished = 1,
    Unnegotiated = 2,
    NotEstablished = 3,
    SessionBlocked = 4,
    NegotiationInProgress = 5,
    EstablishInProgress = 6,
    KeepaliveIntervalLapsed = 10,
    InvalidSessionId = 11,
    InvalidSessionVerId = 12,
    InvalidTimestamp = 13,
    InvalidNextSeqNo = 14,
    UnrecognizedMessage = 15,
    InvalidSofh = 16,
    DecodingError = 17,
    TerminateNotAllowed = 20,
    TerminateInProgress = 21,
    ProtocolVersionNotSupported = 23,
    BackupTakeoverInProgress = 30,
}

/// <summary>
/// Cancel-on-disconnect behaviour negotiated with the gateway (schema enum
/// <c>CancelOnDisconnectType</c>). Sent in <c>Negotiate</c>; the gateway
/// applies it when it observes a TCP disconnect and/or <c>Terminate</c>.
/// </summary>
public enum CancelOnDisconnectType : byte
{
    DoNotCancelOnDisconnectOrTerminate = 0,
    CancelOnDisconnectOnly = 1,
    CancelOnTerminateOnly = 2,
    CancelOnDisconnectOrTerminate = 3,
}
