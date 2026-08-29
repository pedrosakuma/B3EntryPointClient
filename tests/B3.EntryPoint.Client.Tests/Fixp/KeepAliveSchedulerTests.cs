using B3.EntryPoint.Client.Fixp;
using B3.EntryPoint.Client.Tests.TestSupport;

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
        // Fires once two ticks have arrived so the test wakes up immediately
        // instead of polling on a wall-clock deadline.
        var twoTicks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ulong> SendAsync(CancellationToken ct)
        {
            var seq = (ulong)(ticks.Count + 1);
            int count;
            lock (ticks)
            {
                ticks.Add(seq);
                count = ticks.Count;
            }
            if (count >= 2)
                twoTicks.TrySetResult();
            return Task.FromResult(seq);
        }
        var scheduler = new KeepAliveScheduler(
            TimeSpan.FromMilliseconds(40),
            SendAsync);
        scheduler.Start();
        try
        {
            // Widened from 5s to 10s (see #245): under CI-runner contention,
            // even a 40ms tick interval can starve past a tight timeout.
            await AsyncAssert.CompletesWithinAsync(twoTicks.Task, TimeSpan.FromSeconds(10), "expected two keep-alive ticks");
        }
        finally
        {
            scheduler.Stop();
            scheduler.Dispose();
        }
        int finalCount;
        lock (ticks) finalCount = ticks.Count;
        Assert.True(finalCount >= 2, $"expected >=2 ticks, got {finalCount}");
    }

    [Fact]
    public async Task SendCallbackThrows_ReportsFailureWithOriginalException()
    {
        var expected = new IOException("write failed");
        var failure = new TaskCompletionSource<KeepAliveFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new KeepAliveScheduler(
            TimeSpan.FromMilliseconds(200),
            _ => Task.FromException<ulong>(expected),
            onFailure: value => failure.TrySetResult(value));

        scheduler.Start();
        var observed = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Dispose();

        Assert.Equal(KeepAliveFailureKind.SendException, observed.Kind);
        Assert.Same(expected, observed.Exception);
    }

    [Fact]
    public async Task SendExceedsLivenessBudget_ReportsTimeout()
    {
        var failure = new TaskCompletionSource<KeepAliveFailure>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new KeepAliveScheduler(
            TimeSpan.FromMilliseconds(80),
            async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return 1UL;
            },
            onFailure: value => failure.TrySetResult(value));

        scheduler.Start();
        var observed = await failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        scheduler.Dispose();

        Assert.Equal(KeepAliveFailureKind.SendTimeout, observed.Kind);
        Assert.IsType<TimeoutException>(observed.Exception);
        Assert.True(observed.SendDuration >= TimeSpan.FromMilliseconds(40));
    }
}
