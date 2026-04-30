namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Client-side FIXP session state per B3 EntryPoint spec §4.5–§4.10.
/// The bootstrap implementation models the path up to <see cref="Established"/>;
/// <see cref="Suspended"/> handling and the full retransmission flow are tracked in
/// follow-up issues.
/// </summary>
public enum FixpClientState
{
    Disconnected,
    TcpConnected,
    Negotiating,
    Negotiated,
    Establishing,
    Established,
    Suspended,
    Terminating,
    Terminated,
}
