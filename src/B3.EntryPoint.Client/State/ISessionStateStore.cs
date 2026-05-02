using System.Text.Json.Serialization;
using B3.EntryPoint.Client.Models;

namespace B3.EntryPoint.Client.State;

/// <summary>
/// Snapshot of FIXP session state needed to perform a warm-restart via
/// <see cref="EntryPointClient.ReconnectAsync"/>. Persisted via
/// <see cref="ISessionStateStore.SaveAsync"/>.
/// </summary>
public sealed record SessionSnapshot
{
    public uint SessionId { get; init; }
    public uint SessionVerId { get; init; }
    public ulong LastOutboundSeqNum { get; init; }
    public ulong LastInboundSeqNum { get; init; }
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>Outstanding orders (ClOrdID -> SecurityID), useful to
    /// reconcile against ER replay after reconnect.</summary>
    public Dictionary<string, ulong> OutstandingOrders { get; init; } = new();
}

/// <summary>Incremental update appended between snapshots.</summary>
[JsonDerivedType(typeof(OutboundDelta), typeDiscriminator: "out")]
[JsonDerivedType(typeof(InboundDelta), typeDiscriminator: "in")]
[JsonDerivedType(typeof(OrderClosedDelta), typeDiscriminator: "close")]
public abstract record SessionDelta
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record OutboundDelta(ulong SeqNum, string ClOrdID, ulong SecurityId) : SessionDelta;
public sealed record InboundDelta(ulong SeqNum) : SessionDelta;
/// <summary>
/// Marks an order as terminal (cancelled / rejected / fully-filled). Carries
/// the strongly-typed <see cref="Models.ClOrdID"/> directly to avoid a
/// per-event <c>ulong.ToString()</c> allocation on the inbound hot path
/// (#128). Wire format change vs. v0.13.0: serialized as a JSON number
/// (the underlying <c>uint64</c>) instead of a JSON string. The
/// <see cref="ClOrdIDJsonConverter"/> still accepts the legacy string form
/// when replaying snapshots written by older versions.
/// </summary>
public sealed record OrderClosedDelta(ClOrdID ClOrdID) : SessionDelta;

/// <summary>
/// Pluggable persistence for warm-restart. Default implementation is
/// <see cref="FileSessionStateStore"/>.
/// </summary>
public interface ISessionStateStore
{
    ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default);
    ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default);
    ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default);

    /// <summary>Re-builds a logical snapshot from the most recent persisted snapshot
    /// + appended deltas. Used by recovery after a crash.</summary>
    ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default);

    /// <summary>Compacts the delta log into a fresh snapshot, then truncates the deltas.</summary>
    ValueTask CompactAsync(CancellationToken ct = default);
}
