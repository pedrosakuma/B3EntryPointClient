using B3.EntryPoint.Client.Fixp;

namespace B3.EntryPoint.Client.Tests.Fixp;

public class KeepAliveSchedulerTests
{
    [Fact]
    public void Ctor_RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeepAliveScheduler(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new KeepAliveScheduler(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Ctor_StoresInterval()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), s.KeepAliveInterval);
    }

    [Fact]
    public void Start_WithoutBoundTransport_Throws()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(1));
        var ex = Assert.Throws<InvalidOperationException>(s.Start);
        Assert.Contains("transport", ex.Message);
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(1));
        s.Stop();
        s.Stop();
    }

    [Fact]
    public void RaiseFrameSent_FiresEvent()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(1));
        SequenceFrameEventArgs? captured = null;
        s.SequenceFrameSent += (_, e) => captured = e;
        var at = DateTimeOffset.UtcNow;
        s.RaiseFrameSent(42, at);
        Assert.NotNull(captured);
        Assert.Equal(42UL, captured!.NextSeqNo);
        Assert.Equal(at, captured.At);
    }

    [Fact]
    public void RaiseFrameReceived_FiresEvent()
    {
        var s = new KeepAliveScheduler(TimeSpan.FromSeconds(1));
        SequenceFrameEventArgs? captured = null;
        s.SequenceFrameReceived += (_, e) => captured = e;
        s.RaiseFrameReceived(7, DateTimeOffset.UtcNow);
        Assert.NotNull(captured);
        Assert.Equal(7UL, captured!.NextSeqNo);
    }

    [Fact]
    public void IsExposedAsInterface()
    {
        IKeepAliveScheduler s = new KeepAliveScheduler(TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), s.KeepAliveInterval);
    }
}

public class KeepAliveSchedulerPeriodicTests
{
    [Fact]
    public async Task Start_WithBoundTransport_InvokesSendCallbackPeriodically()
    {
        var ticks = new List<ulong>();
        ulong nextSeq = 0;
        Task SendAsync(ulong seq, CancellationToken ct)
        {
            lock (ticks) ticks.Add(seq);
            return Task.CompletedTask;
        }
        var ctorInfo = typeof(B3.EntryPoint.Client.Fixp.KeepAliveScheduler).GetConstructors(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 3);
        var scheduler = (B3.EntryPoint.Client.Fixp.KeepAliveScheduler)ctorInfo.Invoke(new object?[]
        {
            TimeSpan.FromMilliseconds(40),
            (Func<ulong, CancellationToken, Task>)SendAsync,
            (Func<ulong>)(() => System.Threading.Interlocked.Increment(ref nextSeq)),
        });
        scheduler.Start();
        await Task.Delay(180);
        scheduler.Stop();
        scheduler.Dispose();
        Assert.True(ticks.Count >= 2, $"expected >=2 ticks, got {ticks.Count}");
    }
}
