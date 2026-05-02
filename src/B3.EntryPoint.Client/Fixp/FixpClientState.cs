namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Client-side FIXP session state per B3 EntryPoint spec §4.5–§4.10.
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
