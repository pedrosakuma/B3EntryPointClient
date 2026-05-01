using B3.EntryPoint.Client.Fixp;

namespace B3.EntryPoint.Client.Tests.Fixp;

public class RetransmitRequestHandlerTests
{
    [Fact]
    public async Task Request_RejectsZeroCount()
    {
        var h = new RetransmitRequestHandler();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.RequestRetransmitAsync(1, 0));
    }

    [Fact]
    public async Task Request_RejectsCountAboveMax()
    {
        var h = new RetransmitRequestHandler();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.RequestRetransmitAsync(1, RetransmitRequestHandler.MaxCountPerRequest + 1));
    }

    [Fact]
    public async Task Request_RejectsZeroFromSeqNo()
    {
        var h = new RetransmitRequestHandler();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            h.RequestRetransmitAsync(0, 1));
    }

    [Fact]
    public async Task Request_ValidRange_ThrowsNotImplemented()
    {
        var h = new RetransmitRequestHandler();
        var ex = await Assert.ThrowsAsync<NotImplementedException>(() =>
            h.RequestRetransmitAsync(1, 1));
        Assert.Contains("issue #5", ex.Message);
    }

    [Fact]
    public void Events_RoundTripThroughRaiseHooks()
    {
        var h = new RetransmitRequestHandler();
        RetransmitRequestedEventArgs? req = null;
        RetransmissionEventArgs? resp = null;
        RetransmitRejectedEventArgs? rej = null;
        NotAppliedEventArgs? na = null;
        h.RetransmitRequested += (_, e) => req = e;
        h.RetransmissionReceived += (_, e) => resp = e;
        h.RetransmitRejected += (_, e) => rej = e;
        h.NotAppliedReceived += (_, e) => na = e;

        h.RaiseRetransmitRequested(10, 5, DateTimeOffset.UnixEpoch);
        h.RaiseRetransmissionReceived(10, 5, DateTimeOffset.UnixEpoch);
        h.RaiseRetransmitRejected(RetransmitRejectCode.SystemBusy, DateTimeOffset.UnixEpoch);
        h.RaiseNotAppliedReceived(20, 3);

        Assert.Equal((10UL, 5U), (req!.FromSeqNo, req.Count));
        Assert.Equal((10UL, 5U), (resp!.NextSeqNo, resp.Count));
        Assert.Equal(RetransmitRejectCode.SystemBusy, rej!.Code);
        Assert.Equal((20UL, 3U), (na!.FromSeqNo, na.Count));
    }

    [Fact]
    public void IsExposedAsInterface()
    {
        IRetransmitRequestHandler h = new RetransmitRequestHandler();
        Assert.NotNull(h);
    }
}
