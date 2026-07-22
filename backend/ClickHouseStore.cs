using System.Collections.Concurrent;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;

namespace QubicAtlas;

// ClickHouse persistence for Atlas results.
//
// Storage philosophy: we store ONLY OUR RESULTS, not raw proof data. Raw proofs
// (miningSeed/nonce) live on the RPC and are fetched on demand; the ~212KB reconstruction
// blobs stay OUT of ClickHouse (they remain the regenerable on-disk .cache). The single
// source of aggregation is `verifications` (our recomputed verdicts), with epoch/computor/
// tick embedded so browse, leaderboard and epoch stats all derive from it.
//
// If CLICKHOUSE_URL is unset the store is DISABLED and every method is a safe no-op — the
// API then falls back to the file/RPC behavior and never hard-crashes. Every ClickHouse
// call is wrapped so a transient outage degrades gracefully rather than failing a request.
public sealed class ClickHouseStore
{
    public bool Enabled { get; private set; }
    private readonly string _db;
    private readonly string _connString;     // scoped to the atlas database (for reads/writes)
    private readonly string _rootConnString; // no database (for CREATE DATABASE)

    public sealed record VerificationRow(
        string Hash, int Epoch, long Tick, DateTime Ts, string Algorithm, string ComputorId,
        int Score, bool Passes, long Threshold, string GenomeId, string VerifierVersion,
        string Status, int Confirmations, string Spark, DateTime VerifiedAt);

    public sealed record WorkerResultRow(
        string Hash, string WorkerId, string GenomeId, int Score, string VerifierVersion, DateTime At,
        string Signature = "");

    public sealed record EpochRow(
        int Epoch, string CoreVersion, string MiningSeed, long FirstTick, long LastTick,
        long HiThreshold, long AddThreshold);

    // user/password, when non-empty, take precedence over any credentials embedded in the URL —
    // set them via CLICKHOUSE_USER / CLICKHOUSE_PASSWORD so the deploy doesn't have to stuff
    // credentials (and URL-escape special chars) into CLICKHOUSE_URL.
    public ClickHouseStore(string? url, string db, string? user = null, string? password = null)
    {
        _db = string.IsNullOrWhiteSpace(db) ? "atlas" : db;
        if (string.IsNullOrWhiteSpace(url))
        {
            Enabled = false;
            _connString = _rootConnString = "";
            return;
        }
        var uri = new Uri(url);
        string host = uri.Host;
        int cport = uri.Port > 0 ? uri.Port : 8123;
        string proto = uri.Scheme == "https" ? "https" : "http";
        string user2 = string.IsNullOrEmpty(uri.UserInfo) ? "default" : uri.UserInfo.Split(':')[0];
        string pass2 = uri.UserInfo.Contains(':') ? uri.UserInfo.Split(':', 2)[1] : "";
        // explicit env credentials win over URL-embedded ones
        user = string.IsNullOrEmpty(user) ? user2 : user;
        var pass = string.IsNullOrEmpty(password) ? pass2 : password;
        string Base(string? dbName) =>
            $"Host={host};Port={cport};Username={user};Password={pass};Protocol={proto}" +
            (dbName is null ? "" : $";Database={dbName}");
        // Bootstrap connection for CREATE DATABASE: point it at the always-present "default" DB.
        // With no Database at all the driver can't open a session, so a fresh ClickHouse could
        // never be initialised (it only worked where the database already existed).
        _rootConnString = Base("default");
        _connString = Base(_db);
        Enabled = true;
    }

    private ClickHouseConnection Conn() => new(_connString);

    // Create the database + schema on startup if absent. Retries a few times so the API can
    // start slightly ahead of a still-warming ClickHouse (compose healthcheck notwithstanding).
    public async Task InitAsync(int attempts = 12)
    {
        if (!Enabled) return;
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                await using (var root = new ClickHouseConnection(_rootConnString))
                {
                    await root.OpenAsync();
                    await Exec(root, $"CREATE DATABASE IF NOT EXISTS {_db}");
                }
                await using var c = Conn();
                await c.OpenAsync();
                await Exec(c, $@"CREATE TABLE IF NOT EXISTS {_db}.epochs (
  epoch UInt16, core_version LowCardinality(String), mining_seed FixedString(64) CODEC(ZSTD),
  first_tick UInt32, last_tick UInt32, hi_threshold UInt32, add_threshold UInt32
) ENGINE = ReplacingMergeTree ORDER BY epoch");
                await Exec(c, $@"CREATE TABLE IF NOT EXISTS {_db}.verifications (
  hash FixedString(60) CODEC(ZSTD), epoch UInt16, tick UInt32, ts DateTime,
  algorithm LowCardinality(String), computor_id FixedString(60) CODEC(ZSTD),
  score Int32, passes UInt8, threshold UInt32,
  genome_id String CODEC(ZSTD),
  verifier_version LowCardinality(String),
  status LowCardinality(String),
  confirmations UInt8, spark String CODEC(ZSTD), verified_at DateTime
) ENGINE = ReplacingMergeTree(verified_at) ORDER BY (epoch, computor_id, hash)");
                await Exec(c, $@"CREATE TABLE IF NOT EXISTS {_db}.worker_results (
  hash FixedString(60) CODEC(ZSTD), worker_id LowCardinality(String), genome_id String CODEC(ZSTD),
  score Int32, verifier_version LowCardinality(String), at DateTime,
  signature String CODEC(ZSTD)
) ENGINE = MergeTree ORDER BY (hash, at) TTL at + INTERVAL 180 DAY");
                // Backfill sweep progress: the lowest tick FULLY swept per epoch. The historical sweep
                // is strictly descending within an epoch, so one low-water mark per epoch is exactly
                // equivalent to marking every tick — at ~27 rows instead of ~20M. Persisting it is what
                // stops a restart/redeploy from re-sweeping (one RPC round-trip per tick) work already done.
                await Exec(c, $@"CREATE TABLE IF NOT EXISTS {_db}.backfill_progress (
  epoch UInt16, low_tick UInt32, updated_at DateTime
) ENGINE = ReplacingMergeTree(updated_at) ORDER BY epoch");
                // Additive migration for pre-existing worker_results tables (no-op if present).
                await Exec(c, $"ALTER TABLE {_db}.worker_results ADD COLUMN IF NOT EXISTS signature String CODEC(ZSTD)");
                await Exec(c, $"ALTER TABLE {_db}.verifications ADD COLUMN IF NOT EXISTS spark String CODEC(ZSTD)");
                // The dedup check looks up by hash alone, but the primary key is
                // (epoch, computor_id, hash) — hash isn't a usable prefix, so those lookups scan the
                // whole table. A bloom filter lets them skip granules instead. Applies to new parts;
                // run MATERIALIZE INDEX idx_hash once if you want it backfilled over existing data.
                await Exec(c, $"ALTER TABLE {_db}.verifications ADD INDEX IF NOT EXISTS idx_hash hash TYPE bloom_filter(0.01) GRANULARITY 4");
                Console.WriteLine($"[clickhouse] schema ready on {_db}");
                _flushLoop ??= Task.Run(FlushLoop);
                return;
            }
            catch (Exception e)
            {
                last = e;
                Console.WriteLine($"[clickhouse] init attempt {i + 1}/{attempts} failed: {e.Message}");
                await Task.Delay(2000);
            }
        }
        Enabled = false; // degrade to fallback mode rather than crash the process
        Console.WriteLine($"[clickhouse] DISABLED after {attempts} failed init attempts: {last?.Message}");
    }

    private static async Task Exec(ClickHouseConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static readonly string[] VerifCols =
        { "hash", "epoch", "tick", "ts", "algorithm", "computor_id", "score", "passes",
          "threshold", "genome_id", "verifier_version", "status", "confirmations", "spark", "verified_at" };
    private static readonly string[] WorkerCols =
        { "hash", "worker_id", "genome_id", "score", "verifier_version", "at", "signature" };

    // ---- buffered writes ----
    // ClickHouse degrades badly under many tiny inserts: each one creates a part, and the background
    // merges then burn CPU forever compacting them (worse on ReplacingMergeTree, which dedups on
    // merge). At ~1200 proofs/min a row-at-a-time writer produced ~2400 parts/min. Rows are buffered
    // and written in bulk instead — same durability story (a crash loses at most _flushMs of work,
    // which simply gets re-verified), a fraction of the merge load.
    private readonly object _bufLock = new();
    private readonly List<object?[]> _verifBuf = new();
    private readonly List<object?[]> _workerBuf = new();
    // Verdicts for rows not yet flushed. Without this, a proof verified moments ago is invisible to
    // the dedup check and gets re-enqueued — so the buffer must be consulted alongside the table.
    private readonly Dictionary<string, Verdict> _bufferedVerdicts = new();
    private Task? _flushLoop;
    private const int FlushMs = 1000;
    private const int FlushMaxRows = 1000;

    private async Task FlushLoop()
    {
        while (true)
        {
            try { await Task.Delay(FlushMs); await FlushAsync(); }
            catch (Exception e) { Console.WriteLine($"[clickhouse] flush loop: {e.Message}"); }
        }
    }

    public async Task FlushAsync()
    {
        List<object?[]>? v = null, w = null; List<string>? flushedHashes = null;
        lock (_bufLock)
        {
            if (_verifBuf.Count > 0) { v = new(_verifBuf); _verifBuf.Clear(); flushedHashes = _bufferedVerdicts.Keys.ToList(); }
            if (_workerBuf.Count > 0) { w = new(_workerBuf); _workerBuf.Clear(); }
        }
        if (v is not null) await BulkWrite("verifications", VerifCols, v);
        if (w is not null) await BulkWrite("worker_results", WorkerCols, w);
        // Drop buffered verdicts even if the write failed — they're now in the table (or lost and will
        // be re-verified); retaining them would grow unbounded.
        if (flushedHashes is not null)
            lock (_bufLock) foreach (var h in flushedHashes) _bufferedVerdicts.Remove(h);
    }

    private async Task BulkWrite(string table, string[] cols, List<object?[]> rows)
    {
        try
        {
            await using var c = Conn();
            await c.OpenAsync();
            using var bulk = new ClickHouseBulkCopy(c)
            {
                DestinationTableName = $"{_db}.{table}",
                ColumnNames = cols,
                BatchSize = Math.Max(1, rows.Count),
            };
            await bulk.InitAsync();
            await bulk.WriteToServerAsync(rows);
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] bulk insert {table} ({rows.Count} rows) failed: {e.Message}"); }
    }

    public Task UpsertVerificationAsync(VerificationRow r)
    {
        if (!Enabled) return Task.CompletedTask;
        var row = new object?[] { r.Hash, (ushort)r.Epoch, (uint)r.Tick, r.Ts, r.Algorithm, r.ComputorId,
            r.Score, (byte)(r.Passes ? 1 : 0), (uint)r.Threshold, r.GenomeId, r.VerifierVersion,
            r.Status, (byte)Math.Clamp(r.Confirmations, 0, 255), r.Spark ?? "", r.VerifiedAt };
        bool full;
        var verdict = new Verdict(r.Status, r.Score, r.Confirmations, r.GenomeId, r.Passes, r.Spark ?? "");
        Remember(r.Hash);            // known verified from this instant — never ask ClickHouse again
        CacheVerdict(r.Hash, verdict);
        lock (_bufLock)
        {
            _verifBuf.Add(row);
            _bufferedVerdicts[r.Hash] = verdict;
            full = _verifBuf.Count >= FlushMaxRows;
        }
        return full ? FlushAsync() : Task.CompletedTask;
    }

    public Task InsertWorkerResultAsync(WorkerResultRow r)
    {
        if (!Enabled) return Task.CompletedTask;
        var row = new object?[] { r.Hash, r.WorkerId, r.GenomeId, r.Score, r.VerifierVersion, r.At, r.Signature };
        bool full;
        lock (_bufLock) { _workerBuf.Add(row); full = _workerBuf.Count >= FlushMaxRows; }
        return full ? FlushAsync() : Task.CompletedTask;
    }

    private readonly HashSet<int> _epochsSeen = new();
    public async Task UpsertEpochAsync(EpochRow r)
    {
        if (!Enabled) return;
        lock (_epochsSeen) { if (!_epochsSeen.Add(r.Epoch)) return; } // once per process is enough
        try
        {
            // Once per epoch per process — a direct single-row write is fine here (not a hot path).
            await BulkWrite("epochs",
                new[] { "epoch", "core_version", "mining_seed", "first_tick", "last_tick", "hi_threshold", "add_threshold" },
                new List<object?[]> { new object?[] { (ushort)r.Epoch, r.CoreVersion,
                    (r.MiningSeed.Length == 64 ? r.MiningSeed : new string('0', 64)),
                    (uint)r.FirstTick, (uint)r.LastTick, (uint)r.HiThreshold, (uint)r.AddThreshold } });
        }
        catch (Exception e) { lock (_epochsSeen) { _epochsSeen.Remove(r.Epoch); } Console.WriteLine($"[clickhouse] upsert epoch failed: {e.Message}"); }
    }

    // Connection pool. Query() used to open a NEW connection every call, and each open costs a
    // "SELECT version(), timezone()" handshake plus HTTP/auth setup — 52 of those a second in
    // production. Connections are reused instead. The pool hands each one out exclusively, so only
    // one command ever runs on a connection at a time (ADO connections aren't safe to share).
    private readonly ConcurrentBag<ClickHouseConnection> _pool = new();
    private const int PoolMax = 8;

    private async Task<ClickHouseConnection> RentAsync()
    {
        while (_pool.TryTake(out var pooled))
        {
            if (pooled.State == System.Data.ConnectionState.Open) return pooled;
            try { pooled.Dispose(); } catch { }
        }
        var c = Conn();
        await c.OpenAsync();
        return c;
    }

    private void ReturnConn(ClickHouseConnection c)
    {
        if (c.State == System.Data.ConnectionState.Open && _pool.Count < PoolMax) _pool.Add(c);
        else { try { c.Dispose(); } catch { } }
    }

    private async Task<List<object?[]>> Query(string sql, int cols)
    {
        var rows = new List<object?[]>();
        var c = await RentAsync();
        try
        {
            await using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = sql;
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var r = new object?[cols];
                    for (int i = 0; i < cols; i++) r[i] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    rows.Add(r);
                }
            }
            ReturnConn(c);
            return rows;
        }
        catch
        {
            try { c.Dispose(); } catch { }   // never return a possibly-broken connection to the pool
            throw;
        }
    }

    private static long L(object? o) => o is null ? 0 : Convert.ToInt64(o);
    private static string S(object? o) => o?.ToString() ?? "";

    // Leaderboard: distinct verified proofs per computor (deduped via FINAL).
    public async Task<List<(string ComputorId, long Solutions, long FirstTick, long LastTick)>?> LeaderboardAsync()
    {
        if (!Enabled) return null;
        try
        {
            var rows = await Query(
                $"SELECT computor_id, count() AS n, min(tick), max(tick) FROM {_db}.verifications FINAL " +
                "GROUP BY computor_id ORDER BY n DESC", 4);
            return rows.Select(r => (S(r[0]).TrimEnd('\0'), L(r[1]), L(r[2]), L(r[3]))).ToList();
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] leaderboard failed: {e.Message}"); return null; }
    }

    // Contributor leaderboard: per verifier identity, total submissions + how many matched the
    // confirmed genome. Only real signed identities (60-char). Ranked by correct contributions.
    // Decided = submissions to proofs that actually REACHED consensus (have a verifications row);
    // accuracy is correct/decided, not correct/total — a submission to a proof still pending (no
    // consensus yet) is neither right nor wrong and must not count against the worker.
    public async Task<List<(string Id, long Verifications, long Correct, long Decided, DateTime FirstSeen, DateTime LastSeen)>?> VerifierLeaderboardAsync(int limit)
    {
        if (!Enabled) return null;
        try
        {
            // Heaviest query in the system: a full scan of worker_results joined against a full
            // GROUP BY of verifications, and it grows with the dataset. A leaderboard doesn't need
            // to be second-accurate, so serve it from a short cache instead of per request.
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lbCache is not null && _lbLimit == limit && nowMs - _lbAt < 30_000) return _lbCache;
            var rows = await Query(
                $"SELECT wr.worker_id, count() AS n, " +
                $"countIf(v.genome_id != '' AND wr.genome_id = v.genome_id) AS correct, " +
                $"countIf(v.genome_id != '') AS decided, min(wr.at), max(wr.at) " +
                $"FROM {_db}.worker_results wr " +
                $"LEFT JOIN (SELECT hash, argMax(genome_id, verified_at) AS genome_id FROM {_db}.verifications GROUP BY hash) v " +
                $"ON wr.hash = v.hash " +
                $"WHERE length(wr.worker_id) = 60 " +
                $"GROUP BY wr.worker_id ORDER BY correct DESC, n DESC LIMIT {Math.Clamp(limit, 1, 500)}", 6);
            _lbCache = rows.Select(r => (S(r[0]).TrimEnd('\0'), L(r[1]), L(r[2]), L(r[3]),
                Convert.ToDateTime(r[4]), Convert.ToDateTime(r[5]))).ToList();
            _lbAt = nowMs; _lbLimit = limit;
            return _lbCache;
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] verifier leaderboard failed: {e.Message}"); return null; }
    }

    private List<(string, long, long, long, DateTime, DateTime)>? _lbCache;
    private long _lbAt; private int _lbLimit = -1;

    // Single computor overview from our verified proofs.
    private static string OnlyAZ(string s) => new string(s.Where(c => c >= 'A' && c <= 'Z').ToArray());

    public async Task<(long Total, long FirstTick, long LastTick, long Hi, long Add)?> ComputorSummaryAsync(string id)
    {
        if (!Enabled) return null;
        var cid = OnlyAZ(id); if (cid.Length != 60) return null;
        try
        {
            var r = await Query(
                $"SELECT count(), min(tick), max(tick), countIf(algorithm='HyperIdentity'), countIf(algorithm='Addition') " +
                $"FROM {_db}.verifications FINAL WHERE computor_id = '{cid}'", 5);
            if (r.Count == 0) return (0, 0, 0, 0, 0);
            return (L(r[0][0]), L(r[0][1]), L(r[0][2]), L(r[0][3]), L(r[0][4]));
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] computor summary failed: {e.Message}"); return null; }
    }

    public async Task<List<(string Hash, int Epoch, long Tick, string Algorithm, int Score, string Status, string Spark, long TsMs)>?> ComputorRecentAsync(string id, int limit)
    {
        if (!Enabled) return null;
        var cid = OnlyAZ(id); if (cid.Length != 60) return null;
        try
        {
            var rows = await Query(
                $"SELECT hash, epoch, tick, algorithm, score, status, spark, toUnixTimestamp(ts) FROM {_db}.verifications FINAL " +
                $"WHERE computor_id = '{cid}' ORDER BY tick DESC LIMIT {Math.Clamp(limit, 1, 200)}", 8);
            return rows.Select(r => (S(r[0]).TrimEnd('\0'), (int)L(r[1]), L(r[2]), S(r[3]), (int)L(r[4]), S(r[5]), S(r[6]), L(r[7]) * 1000)).ToList();
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] computor recent failed: {e.Message}"); return null; }
    }

    // Recent verified proofs (each has a spark) — powers the "Verified only" list view.
    // TsMs = the proof's on-chain time in unix ms (for the "when" display), 0 if unknown.
    public async Task<List<(string Hash, int Epoch, long Tick, string Algorithm, string ComputorId, int Score, int Confirmations, string Status, string Spark, long TsMs)>?>
        VerifiedProofsAsync(int limit, int offset, string? algo, int? epoch)
    {
        if (!Enabled) return null;
        try
        {
            var where = "status IN ('confirmed','conflicted')";
            if (!string.IsNullOrEmpty(algo) && (algo == "HyperIdentity" || algo == "Addition")) where += $" AND algorithm = '{algo}'";
            if (epoch.HasValue) where += $" AND epoch = {epoch.Value}";
            var rows = await Query(
                $"SELECT hash, epoch, tick, algorithm, computor_id, score, confirmations, status, spark, toUnixTimestamp(ts) " +
                $"FROM {_db}.verifications FINAL WHERE {where} ORDER BY tick DESC " +
                $"LIMIT {Math.Clamp(limit, 1, 100)} OFFSET {Math.Clamp(offset, 0, 100000)}", 10);
            return rows.Select(r => (S(r[0]).TrimEnd('\0'), (int)L(r[1]), L(r[2]), S(r[3]), S(r[4]).TrimEnd('\0'),
                (int)L(r[5]), (int)L(r[6]), S(r[7]), S(r[8]), L(r[9]) * 1000)).ToList();
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] verified proofs failed: {e.Message}"); return null; }
    }

    // Cached aggregates — these scan the table, and both the dashboard and the backfill scheduler
    // ask for them on a timer, so an uncached call per request multiplied the load for no new data.
    private (long, long, long, long, long)? _statsCache; private long _statsAt;
    private Dictionary<int, (long Verified, long Confirmed, long Conflicted)>? _epochCache; private long _epochAt;

    // epoch -> verified/confirmed/conflicted counts.
    public async Task<Dictionary<int, (long Verified, long Confirmed, long Conflicted)>?> EpochCountsAsync()
    {
        if (!Enabled) return null;
        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_epochCache is not null && nowMs - _epochAt < 30_000) return _epochCache;
            var rows = await Query(
                $"SELECT epoch, uniqExact(hash), uniqExactIf(hash, status='confirmed'), " +
                $"uniqExactIf(hash, status='conflicted') " +
                $"FROM {_db}.verifications GROUP BY epoch", 4);
            _epochCache = rows.ToDictionary(r => (int)L(r[0]), r => (L(r[1]), L(r[2]), L(r[3])));
            _epochAt = nowMs;
            return _epochCache;
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] epoch counts failed: {e.Message}"); return null; }
    }

    // epoch -> lowest tick FULLY swept by the historical backfill (its resume point).
    public async Task<Dictionary<int, long>?> BackfillProgressAsync()
    {
        if (!Enabled) return null;
        try
        {
            var rows = await Query($"SELECT epoch, low_tick FROM {_db}.backfill_progress FINAL", 2);
            return rows.ToDictionary(r => (int)L(r[0]), r => L(r[1]));
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] backfill progress read failed: {e.Message}"); return null; }
    }

    // Record how far down an epoch has been fully swept. Only ever moves DOWN (a higher low_tick for
    // an epoch we've already swept past would lose ground), so the caller passes the new minimum.
    public async Task SaveBackfillProgressAsync(int epoch, long lowTick)
    {
        if (!Enabled) return;
        try
        {
            await using var c = Conn();
            await c.OpenAsync();
            await Exec(c, $"INSERT INTO {_db}.backfill_progress (epoch, low_tick, updated_at) " +
                          $"VALUES ({epoch}, {Math.Max(0, lowTick)}, now())");
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] backfill progress write failed: {e.Message}"); }
    }

    public sealed record Verdict(string Status, int Score, int Confirmations, string GenomeId, bool Passes, string Spark);

    // ---- known-verified hash set ----
    // The feed loops re-scan the SAME newest proofs every couple of seconds, so "have we verified
    // this hash?" was hitting ClickHouse dozens of times a second forever — pure CPU burn with no
    // new information. Once a hash is known verified that answer never changes, so it's cached in
    // memory. Bounded + FIFO-evicted so it can't grow unbounded.
    private readonly ConcurrentDictionary<string, byte> _known = new();
    private readonly ConcurrentQueue<string> _knownOrder = new();
    private const int KnownMax = 200_000;

    private void Remember(string hash)
    {
        if (string.IsNullOrEmpty(hash) || !_known.TryAdd(hash, 1)) return;
        _knownOrder.Enqueue(hash);
        while (_knownOrder.Count > KnownMax && _knownOrder.TryDequeue(out var old)) _known.TryRemove(old, out _);
    }

    // Full verdicts, cached. A row lands in `verifications` only at finalize and is terminal from
    // then on, so serving it from memory is safe — and the proof list/detail endpoints ask about the
    // same newest hashes over and over (28k lookups/10min in production, the single biggest query
    // load). Only POSITIVE results are cached: a hash with no row yet must keep hitting the table,
    // otherwise a proof would look unverified forever.
    private readonly ConcurrentDictionary<string, Verdict> _verdictCache = new();
    private readonly ConcurrentQueue<string> _verdictOrder = new();
    private const int VerdictCacheMax = 50_000;

    private void CacheVerdict(string hash, Verdict v)
    {
        if (string.IsNullOrEmpty(hash)) return;
        if (_verdictCache.TryAdd(hash, v))
        {
            _verdictOrder.Enqueue(hash);
            while (_verdictOrder.Count > VerdictCacheMax && _verdictOrder.TryDequeue(out var old))
                _verdictCache.TryRemove(old, out _);
        }
        else _verdictCache[hash] = v;   // refresh in place, don't re-queue
    }

    // Existence-only check for the feed loops: which of these hashes are already verified?
    // Answers from memory where possible and queries ClickHouse ONLY for hashes it hasn't seen —
    // so a steady-state re-scan of already-verified proofs costs zero queries.
    public async Task<HashSet<string>> KnownHashesAsync(IEnumerable<string> hashes)
    {
        var result = new HashSet<string>();
        var need = new List<string>();
        foreach (var h in hashes)
        {
            if (string.IsNullOrEmpty(h)) continue;
            if (_known.ContainsKey(h)) result.Add(h);
            else need.Add(h);
        }
        if (!Enabled || need.Count == 0) return result;
        try
        {
            var inClause = string.Join(",", need.Distinct().Select(h => "'" + h.Replace("'", "") + "'"));
            var rows = await Query($"SELECT DISTINCT hash FROM {_db}.verifications WHERE hash IN ({inClause})", 1);
            foreach (var r in rows)
            {
                var h = S(r[0]).TrimEnd('\0');
                if (string.IsNullOrEmpty(h)) continue;
                result.Add(h); Remember(h);
            }
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] known hashes failed: {e.Message}"); }
        return result;
    }

    public async Task<Dictionary<string, Verdict>?> VerdictsForAsync(IEnumerable<string> hashes)
    {
        if (!Enabled) return null;
        var list = hashes.Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
        if (list.Count == 0) return new();
        try
        {
            // Serve what we can from memory; only unknown hashes reach ClickHouse.
            var map0 = new Dictionary<string, Verdict>();
            var miss = new List<string>();
            foreach (var h in list)
            {
                if (_verdictCache.TryGetValue(h, out var cv)) map0[h] = cv;
                else miss.Add(h);
            }
            if (miss.Count == 0)
            {
                lock (_bufLock)
                    foreach (var h in list)
                        if (_bufferedVerdicts.TryGetValue(h, out var bv)) map0[h] = bv;
                return map0;
            }
            list = miss;
            var inClause = string.Join(",", list.Select(h => "'" + h.Replace("'", "") + "'"));
            // No FINAL: this answers "have we already verified this hash?", so duplicate versions
            // across parts are harmless — and FINAL forced a merge-on-read of the whole table on a
            // query that runs dozens of times a second.
            var rows = await Query(
                $"SELECT hash, status, score, confirmations, genome_id, passes, spark FROM {_db}.verifications " +
                $"WHERE hash IN ({inClause})", 7);
            var map = map0;
            foreach (var r in rows)
            {
                var h = S(r[0]).TrimEnd('\0');
                var v = new Verdict(S(r[1]), (int)L(r[2]), (int)L(r[3]), S(r[4]), L(r[5]) != 0, S(r[6]));
                map[h] = v;
                CacheVerdict(h, v); Remember(h);
            }
            // Rows still sitting in the write buffer aren't in the table yet — without this a proof
            // verified in the last second would be re-enqueued as unseen.
            lock (_bufLock)
                foreach (var h in list)
                    if (_bufferedVerdicts.TryGetValue(h, out var bv)) map[h] = bv;
            return map;
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] verdicts failed: {e.Message}"); return null; }
    }

    public async Task<Verdict?> VerdictForAsync(string hash)
    {
        var m = await VerdictsForAsync(new[] { hash });
        return m != null && m.TryGetValue(hash, out var v) ? v : null;
    }

    // RecentPerMin = proofs verified in the last 60s (current throughput). Computed on the raw table
    // (not FINAL) so it reflects fresh inserts; a proof's confirmation stamps verified_at ≈ now.
    public async Task<(long Verified, long Confirmed, long Conflicted, long Computors, long RecentPerMin)?> VerificationStatsAsync()
    {
        if (!Enabled) return null;
        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_statsCache is not null && nowMs - _statsAt < 5000) return _statsCache;
            // Distinct-count instead of FINAL: same answer for a dashboard, without forcing a
            // merge-on-read of the entire table on a query every visitor triggers.
            var rows = await Query(
                $"SELECT uniqExact(hash), uniqExactIf(hash, status='confirmed'), " +
                $"uniqExactIf(hash, status='conflicted'), uniqExact(computor_id) " +
                $"FROM {_db}.verifications", 4);
            var rate = await Query($"SELECT uniqExact(hash) FROM {_db}.verifications WHERE verified_at > now() - 60", 1);
            if (rows.Count == 0) return (0, 0, 0, 0, 0);
            var r = rows[0];
            _statsCache = (L(r[0]), L(r[1]), L(r[2]), L(r[3]), L(rate.FirstOrDefault()?[0]));
            _statsAt = nowMs;
            return _statsCache;
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] stats failed: {e.Message}"); return null; }
    }

    public async Task<(long Verifications, long WorkerResults, long Epochs)?> RowCountsAsync()
    {
        if (!Enabled) return null;
        try
        {
            // Raw row counts (no FINAL) — this is a liveness probe, not a reported metric, and FINAL
            // here meant a full merge-scan on every health poll.
            var v = await Query($"SELECT count() FROM {_db}.verifications", 1);
            var w = await Query($"SELECT count() FROM {_db}.worker_results", 1);
            var e = await Query($"SELECT count() FROM {_db}.epochs", 1);
            return (L(v.FirstOrDefault()?[0]), L(w.FirstOrDefault()?[0]), L(e.FirstOrDefault()?[0]));
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] counts failed: {e.Message}"); return null; }
    }
}
