using System.Diagnostics;
using System.Diagnostics.Metrics;
using B3.EntryPoint.Client.Telemetry;

namespace B3.EntryPoint.Client.Tests.Telemetry;

public class EntryPointTelemetryTests
{
    [Fact]
    public void Meter_Exposes_Expected_Instruments()
    {
        var seen = new HashSet<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == EntryPointTelemetry.SourceName)
                {
                    seen.Add(instr.Name);
                    l.EnableMeasurementEvents(instr);
                }
            },
        };
        listener.Start();

        Assert.Contains("entrypoint.orders.submitted", seen);
        Assert.Contains("entrypoint.orders.replaced", seen);
        Assert.Contains("entrypoint.orders.cancelled", seen);
        Assert.Contains("entrypoint.orders.mass_actions", seen);
        Assert.Contains("entrypoint.risk.rejections", seen);
        Assert.Contains("entrypoint.session.terminations", seen);
        Assert.Contains("entrypoint.outbound.latency", seen);
    }

    [Fact]
    public void Counter_Add_Reaches_Listener()
    {
        long total = 0;
        string? lastTag = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instr, l) =>
            {
                if (instr.Meter.Name == EntryPointTelemetry.SourceName && instr.Name == "entrypoint.orders.submitted")
                    l.EnableMeasurementEvents(instr);
            },
        };
        listener.SetMeasurementEventCallback<long>((instr, value, tags, state) =>
        {
            Interlocked.Add(ref total, value);
            foreach (var t in tags)
                if (t.Key == "kind") lastTag = t.Value as string;
        });
        listener.Start();

        EntryPointTelemetry.OrdersSubmitted.Add(3, new KeyValuePair<string, object?>("kind", "NewOrder"));
        listener.RecordObservableInstruments();

        Assert.Equal(3, total);
        Assert.Equal("NewOrder", lastTag);
    }

    [Fact]
    public void ActivitySource_Emits_Activity_When_Listener_Sampled()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == EntryPointTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => captured = a,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = EntryPointTelemetry.ActivitySource.StartActivity("entrypoint.unit-test", ActivityKind.Client);

        Assert.NotNull(activity);
        Assert.Same(activity, captured);
        Assert.Equal("entrypoint.unit-test", captured!.OperationName);
    }
}
