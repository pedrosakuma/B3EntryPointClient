namespace B3.EntryPoint.Client.Fixp;

/// <summary>
/// Stub for the heartbeat/keep-alive scheduler (spec §4.5 BusinessNegotiate
/// keepAliveInterval). Real implementation lands with the Spec_4_6 scenarios.
/// </summary>
internal sealed class KeepAliveScheduler
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
}
