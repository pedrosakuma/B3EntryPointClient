using System.Text.Json;

namespace B3.EntryPoint.Client.State;

/// <summary>
/// File-backed implementation of <see cref="ISessionStateStore"/>. The snapshot
/// is written atomically (temp + rename) to <c>{directory}/snapshot.json</c>;
/// deltas are appended one-JSON-line per record to <c>{directory}/deltas.jsonl</c>.
/// </summary>
public sealed class FileSessionStateStore : ISessionStateStore
{
    private readonly string _snapshotPath;
    private readonly string _deltasPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    public FileSessionStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory.CreateDirectory(directory);
        _snapshotPath = Path.Combine(directory, "snapshot.json");
        _deltasPath = Path.Combine(directory, "deltas.jsonl");
    }

    public async ValueTask<SessionSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_snapshotPath)) return null;
        await using var fs = File.OpenRead(_snapshotPath);
        return await JsonSerializer.DeserializeAsync<SessionSnapshot>(fs, JsonOpts, ct).ConfigureAwait(false);
    }

    public async ValueTask SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tmp = _snapshotPath + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, snapshot, JsonOpts, ct).ConfigureAwait(false);
            File.Move(tmp, _snapshotPath, overwrite: true);
            // Drop deltas — the snapshot now subsumes them.
            if (File.Exists(_deltasPath)) File.Delete(_deltasPath);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask AppendDeltaAsync(SessionDelta delta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var line = JsonSerializer.Serialize<SessionDelta>(delta, JsonOpts) + Environment.NewLine;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_deltasPath, line, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<SessionSnapshot?> ReplayAsync(CancellationToken ct = default)
    {
        var snap = await LoadAsync(ct).ConfigureAwait(false);
        if (snap is null && !File.Exists(_deltasPath)) return null;
        snap ??= new SessionSnapshot { CapturedAt = DateTimeOffset.UtcNow };

        if (!File.Exists(_deltasPath)) return snap;

        var rebuilt = snap;
        var outstanding = new Dictionary<string, ulong>(rebuilt.OutstandingOrders);
        ulong outboundSeq = rebuilt.LastOutboundSeqNum;
        ulong inboundSeq = rebuilt.LastInboundSeqNum;

        await foreach (var line in File.ReadLinesAsync(_deltasPath, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var d = JsonSerializer.Deserialize<SessionDelta>(line, JsonOpts);
            switch (d)
            {
                case OutboundDelta o:
                    outboundSeq = Math.Max(outboundSeq, o.SeqNum);
                    outstanding[o.ClOrdID] = o.SecurityId;
                    break;
                case InboundDelta i:
                    inboundSeq = Math.Max(inboundSeq, i.SeqNum);
                    break;
                case OrderClosedDelta c:
                    // OrderClosedDelta carries a strongly-typed ClOrdID (#128).
                    // The legacy snapshot dict is still string-keyed so we
                    // convert at the boundary; this runs only at replay /
                    // compact time, never on the per-event hot path.
                    outstanding.Remove(c.ClOrdID.ToString());
                    break;
            }
        }
        return rebuilt with
        {
            LastOutboundSeqNum = outboundSeq,
            LastInboundSeqNum = inboundSeq,
            OutstandingOrders = outstanding,
            CapturedAt = DateTimeOffset.UtcNow,
        };
    }

    public async ValueTask CompactAsync(CancellationToken ct = default)
    {
        var rebuilt = await ReplayAsync(ct).ConfigureAwait(false);
        if (rebuilt is null) return;
        await SaveAsync(rebuilt, ct).ConfigureAwait(false);
    }
}
