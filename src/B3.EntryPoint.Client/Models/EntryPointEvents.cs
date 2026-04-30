namespace B3.EntryPoint.Client.Models;

/// <summary>
/// Marker base for events surfaced via <c>EntryPointClient.Events</c>.
/// Concrete shapes are intentionally minimal in the bootstrap; the full
/// ExecutionReport/Reject mapping lands with the Spec_5 scenarios.
/// </summary>
public abstract record EntryPointEvent;

public sealed record ExecutionEvent(string ClOrdID, ulong OrderId) : EntryPointEvent;

public sealed record RejectEvent(string ClOrdID, string Reason) : EntryPointEvent;

public sealed record BusinessRejectEvent(string RefSeqNum, string Reason) : EntryPointEvent;
