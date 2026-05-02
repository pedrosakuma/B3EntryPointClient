namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Pure FIXP client-side state machine. No I/O, no clocks: feed it
/// <see cref="FixpClientTrigger"/> events and read <see cref="State"/>. Invalid
/// transitions throw <see cref="InvalidFixpTransitionException"/> — callers that
/// want lenient behavior should query <see cref="CanFire"/> first.
/// </summary>
public sealed class FixpClientStateMachine
{
    public FixpClientState State { get; private set; } = FixpClientState.Disconnected;

    public bool CanFire(FixpClientTrigger trigger) => Next(State, trigger) is not null;

    public void Fire(FixpClientTrigger trigger)
    {
        var next = Next(State, trigger)
            ?? throw new InvalidFixpTransitionException(State, trigger);
        State = next;
    }

    private static FixpClientState? Next(FixpClientState state, FixpClientTrigger trigger)
    {
        // Terminate paths are valid from any state that owns a TCP connection.
        if (trigger == FixpClientTrigger.TerminateReceived || trigger == FixpClientTrigger.SendTerminate)
        {
            return state == FixpClientState.Disconnected || state == FixpClientState.Terminated
                ? null
                : FixpClientState.Terminating;
        }

        if (trigger == FixpClientTrigger.TransportClosed)
        {
            return state == FixpClientState.Disconnected ? null : FixpClientState.Terminated;
        }

        if (trigger == FixpClientTrigger.ProtocolError)
        {
            return state == FixpClientState.Disconnected || state == FixpClientState.Terminated
                ? null
                : FixpClientState.Terminating;
        }

        return (state, trigger) switch
        {
            (FixpClientState.Disconnected, FixpClientTrigger.TcpConnected) => FixpClientState.TcpConnected,
            (FixpClientState.TcpConnected, FixpClientTrigger.SendNegotiate) => FixpClientState.Negotiating,
            (FixpClientState.Negotiating, FixpClientTrigger.NegotiateResponseReceived) => FixpClientState.Negotiated,
            (FixpClientState.Negotiating, FixpClientTrigger.NegotiateRejectReceived) => FixpClientState.Terminating,
            (FixpClientState.Negotiated, FixpClientTrigger.SendEstablish) => FixpClientState.Establishing,
            (FixpClientState.Establishing, FixpClientTrigger.EstablishAckReceived) => FixpClientState.Established,
            (FixpClientState.Establishing, FixpClientTrigger.EstablishRejectReceived) => FixpClientState.Terminating,
            _ => null,
        };
    }
}

public sealed class InvalidFixpTransitionException(FixpClientState state, FixpClientTrigger trigger)
    : InvalidOperationException($"FIXP client cannot fire trigger '{trigger}' from state '{state}'.")
{
    public FixpClientState State { get; } = state;
    public FixpClientTrigger Trigger { get; } = trigger;
}
