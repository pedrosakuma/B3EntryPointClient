using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace B3.EntryPoint.Client.Telemetry;

/// <summary>
/// Static <see cref="ActivitySource"/> and <see cref="Meter"/> exposed by the
/// EntryPoint client. Consumers attach an OpenTelemetry SDK (or any
/// <see cref="MeterListener"/>/<see cref="ActivityListener"/>) to the names
/// below — the library has no SDK dependency itself.
/// </summary>
public static class EntryPointTelemetry
{
    public const string SourceName = "B3.EntryPoint.Client";
    public const string Version = "0.4.2";

    public static readonly ActivitySource ActivitySource = new(SourceName, Version);
    public static readonly Meter Meter = new(SourceName, Version);

    public static readonly Counter<long> OrdersSubmitted =
        Meter.CreateCounter<long>("entrypoint.orders.submitted", description: "NewOrderSingle/SimpleNewOrder requests sent");
    public static readonly Counter<long> OrdersReplaced =
        Meter.CreateCounter<long>("entrypoint.orders.replaced", description: "OrderCancelReplaceRequest/SimpleModify requests sent");
    public static readonly Counter<long> OrdersCancelled =
        Meter.CreateCounter<long>("entrypoint.orders.cancelled", description: "OrderCancelRequest sent");
    public static readonly Counter<long> MassActions =
        Meter.CreateCounter<long>("entrypoint.orders.mass_actions", description: "OrderMassActionRequest sent");
    public static readonly Counter<long> RiskRejections =
        Meter.CreateCounter<long>("entrypoint.risk.rejections", description: "Outbound requests aborted by a pre-trade gate");
    public static readonly Counter<long> Terminations =
        Meter.CreateCounter<long>("entrypoint.session.terminations", description: "FIXP Terminate sent or received");

    public static readonly Histogram<double> OutboundLatency =
        Meter.CreateHistogram<double>("entrypoint.outbound.latency", unit: "ms",
            description: "Wall clock between Submit/Replace/Cancel entry and frame on the wire");
}

/// <summary>
/// Snapshot consumable by ASP.NET Core <c>IHealthCheck</c> implementations or
/// any liveness probe. Pure data; the consumer decides what is healthy.
/// </summary>
public readonly record struct ClientHealth(
    Fixp.FixpClientState State,
    DateTime LastInboundUtc,
    TimeSpan SinceLastInbound);
