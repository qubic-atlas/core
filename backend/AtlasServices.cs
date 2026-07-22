using System.Text.Json.Nodes;

namespace QubicAtlas;

// One row of the in-memory recent-solution index.
public sealed record IndexRow(string Hash, string Algorithm, string ComputorId, long TickNumber, object? Timestamp);

// In-memory index of recent solutions, self-populated from the RPC firehose (newest-first).
// Previously loaded a jsonl file produced by a separate Node indexer process; now it pages the
// public archive directly so the .NET service is fully standalone — no external indexer.
// Deep history for listings comes from ClickHouse verifications + the backfill scheduler; this
// index just holds the recent unverified tail so filtered listings work before proofs are verified.
public sealed class SolutionIndex
{
    private readonly QubicRpc _rpc;
    private readonly int _cap;
    private volatile List<IndexRow> _rows = new();

    public SolutionIndex(QubicRpc rpc, int cap = 1000) { _rpc = rpc; _cap = cap; }

    public List<IndexRow> Rows => _rows;

    public async Task ReloadAsync()
    {
        try
        {
            var res = await _rpc.ListSolutions(_cap, 0);
            var rows = res.Items
                .Where(s => !string.IsNullOrEmpty(s.Hash))
                .Select(s => new IndexRow(s.Hash!, s.Algorithm, s.ComputorId ?? "", s.TickNumber, s.Timestamp))
                .ToList();
            rows.Sort((a, b) => b.TickNumber.CompareTo(a.TickNumber));
            _rows = rows;
        }
        catch { /* transient RPC hiccup — keep the previous snapshot rather than blanking it */ }
    }
}

// tick -> epoch map, built from /v1/status (cached 60s). Port of index.js epochBoundaries().
public sealed class EpochMap
{
    private readonly QubicRpc _rpc;
    private readonly object _lock = new();
    private long _at;
    private List<(int Epoch, long First, long Last)> _boundaries = new();

    public EpochMap(QubicRpc rpc) => _rpc = rpc;

    public async Task<List<(int Epoch, long First, long Last)>> BoundariesAsync()
    {
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _at < 60000 && _boundaries.Count > 0)
                return _boundaries;
        }
        var s = await _rpc.Status();
        var per = s["lastProcessedTicksPerEpoch"] as JsonObject;
        var rows = new List<(int epoch, long last)>();
        if (per is not null)
            foreach (var kv in per)
                rows.Add((int.Parse(kv.Key), kv.Value!.GetValue<long>()));
        rows.Sort((a, b) => a.epoch.CompareTo(b.epoch));
        long prev = 0;
        var boundaries = new List<(int, long, long)>();
        foreach (var r in rows)
        {
            boundaries.Add((r.epoch, prev + 1, r.last));
            prev = r.last;
        }
        lock (_lock)
        {
            _boundaries = boundaries;
            _at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        return boundaries;
    }

    public async Task<int?> EpochForTickAsync(long tick)
    {
        var b = await BoundariesAsync();
        foreach (var x in b)
            if (tick >= x.First && tick <= x.Last) return x.Epoch;
        return b.Count > 0 ? b[^1].Epoch : (int?)null;
    }
}
