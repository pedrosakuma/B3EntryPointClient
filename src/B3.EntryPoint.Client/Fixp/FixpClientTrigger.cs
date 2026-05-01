namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Inputs that drive <see cref="FixpClientStateMachine"/> transitions. Pure logic,
/// no I/O — the owning <see cref="FixpClientSession"/> translates wire events into
/// these triggers and observes the resulting state to decide what to send next.
/// </summary>
public enum FixpClientTrigger
{
    TcpConnected,
    SendNegotiate,
    NegotiateResponseReceived,
    NegotiateRejectReceived,
    SendEstablish,
    EstablishAckReceived,
    EstablishRejectReceived,
    SendTerminate,
    TerminateReceived,
    TransportClosed,
    ProtocolError,
}
