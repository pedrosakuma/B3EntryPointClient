using B3.EntryPoint.Client.Fixp;

namespace B3.EntryPoint.Client.Tests.Fixp;

public class FixpClientStateMachineTests
{
    [Fact]
    public void HappyPath_ReachesEstablished()
    {
        var sm = new FixpClientStateMachine();
        Assert.Equal(FixpClientState.Disconnected, sm.State);

        sm.Fire(FixpClientTrigger.TcpConnected);
        Assert.Equal(FixpClientState.TcpConnected, sm.State);

        sm.Fire(FixpClientTrigger.SendNegotiate);
        Assert.Equal(FixpClientState.Negotiating, sm.State);

        sm.Fire(FixpClientTrigger.NegotiateResponseReceived);
        Assert.Equal(FixpClientState.Negotiated, sm.State);

        sm.Fire(FixpClientTrigger.SendEstablish);
        Assert.Equal(FixpClientState.Establishing, sm.State);

        sm.Fire(FixpClientTrigger.EstablishAckReceived);
        Assert.Equal(FixpClientState.Established, sm.State);
    }

    [Fact]
    public void NegotiateReject_TransitionsToTerminating()
    {
        var sm = Connected();
        sm.Fire(FixpClientTrigger.SendNegotiate);
        sm.Fire(FixpClientTrigger.NegotiateRejectReceived);
        Assert.Equal(FixpClientState.Terminating, sm.State);
    }

    [Fact]
    public void EstablishReject_TransitionsToTerminating()
    {
        var sm = Negotiated();
        sm.Fire(FixpClientTrigger.SendEstablish);
        sm.Fire(FixpClientTrigger.EstablishRejectReceived);
        Assert.Equal(FixpClientState.Terminating, sm.State);
    }

    [Fact]
    public void TransportClosed_FromEstablished_GoesToTerminated()
    {
        var sm = Established();
        sm.Fire(FixpClientTrigger.TransportClosed);
        Assert.Equal(FixpClientState.Terminated, sm.State);
    }

    [Fact]
    public void Terminate_FromAnyLiveState_IsAllowed()
    {
        foreach (var live in new[]
        {
            FixpClientState.TcpConnected,
            FixpClientState.Negotiating,
            FixpClientState.Negotiated,
            FixpClientState.Establishing,
            FixpClientState.Established,
        })
        {
            var sm = AdvanceTo(live);
            Assert.True(sm.CanFire(FixpClientTrigger.SendTerminate));
            sm.Fire(FixpClientTrigger.SendTerminate);
            Assert.Equal(FixpClientState.Terminating, sm.State);
        }
    }

    [Fact]
    public void InvalidTransition_Throws()
    {
        var sm = new FixpClientStateMachine();
        Assert.False(sm.CanFire(FixpClientTrigger.SendNegotiate));
        var ex = Assert.Throws<InvalidFixpTransitionException>(
            () => sm.Fire(FixpClientTrigger.SendNegotiate));
        Assert.Equal(FixpClientState.Disconnected, ex.State);
        Assert.Equal(FixpClientTrigger.SendNegotiate, ex.Trigger);
    }

    [Fact]
    public void TransportClosed_FromDisconnected_IsRejected()
    {
        var sm = new FixpClientStateMachine();
        Assert.False(sm.CanFire(FixpClientTrigger.TransportClosed));
        Assert.Throws<InvalidFixpTransitionException>(
            () => sm.Fire(FixpClientTrigger.TransportClosed));
    }

    private static FixpClientStateMachine Connected()
    {
        var sm = new FixpClientStateMachine();
        sm.Fire(FixpClientTrigger.TcpConnected);
        return sm;
    }

    private static FixpClientStateMachine Negotiated()
    {
        var sm = Connected();
        sm.Fire(FixpClientTrigger.SendNegotiate);
        sm.Fire(FixpClientTrigger.NegotiateResponseReceived);
        return sm;
    }

    private static FixpClientStateMachine Established()
    {
        var sm = Negotiated();
        sm.Fire(FixpClientTrigger.SendEstablish);
        sm.Fire(FixpClientTrigger.EstablishAckReceived);
        return sm;
    }

    private static FixpClientStateMachine AdvanceTo(FixpClientState target) => target switch
    {
        FixpClientState.TcpConnected => Connected(),
        FixpClientState.Negotiating => Apply(Connected(), FixpClientTrigger.SendNegotiate),
        FixpClientState.Negotiated => Negotiated(),
        FixpClientState.Establishing => Apply(Negotiated(), FixpClientTrigger.SendEstablish),
        FixpClientState.Established => Established(),
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static FixpClientStateMachine Apply(FixpClientStateMachine sm, FixpClientTrigger t)
    {
        sm.Fire(t);
        return sm;
    }
}
