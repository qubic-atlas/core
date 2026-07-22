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
        _rootConnString = Base(null);
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
                // Additive migration for pre-existing worker_results tables (no-op if present).
                await Exec(c, $"ALTER TABLE {_db}.worker_results ADD COLUMN IF NOT EXISTS signature String CODEC(ZSTD)");
                await Exec(c, $"ALTER TABLE {_db}.verifications ADD COLUMN IF NOT EXISTS spark String CODEC(ZSTD)");
                Console.WriteLine($"[clickhouse] schema ready on {_db}");
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

    private async Task Insert(string table, string[] cols, object?[] row)
    {
        await using var c = Conn();
        await c.OpenAsync();
        using var bulk = new ClickHouseBulkCopy(c)
        {
            DestinationTableName = $"{_db}.{table}",
            ColumnNames = cols,
            BatchSize = 1,
        };
        await bulk.InitAsync();
        await bulk.WriteToServerAsync(new[] { row });
    }

    public async Task UpsertVerificationAsync(VerificationRow r)
    {
        if (!Enabled) return;
        try
        {
            await Insert("verifications",
                new[] { "hash", "epoch", "tick", "ts", "algorithm", "computor_id", "score", "passes",
                        "threshold", "genome_id", "verifier_version", "status", "confirmations", "spark", "verified_at" },
                new object?[] { r.Hash, (ushort)r.Epoch, (uint)r.Tick, r.Ts, r.Algorithm, r.ComputorId,
                    r.Score, (byte)(r.Passes ? 1 : 0), (uint)r.Threshold, r.GenomeId, r.VerifierVersion,
                    r.Status, (byte)Math.Clamp(r.Confirmations, 0, 255), r.Spark ?? "", r.VerifiedAt });
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] upsert verification failed: {e.Message}"); }
    }

    public async Task InsertWorkerResultAsync(WorkerResultRow r)
    {
        if (!Enabled) return;
        try
        {
            await Insert("worker_results",
                new[] { "hash", "worker_id", "genome_id", "score", "verifier_version", "at", "signature" },
                new object?[] { r.Hash, r.WorkerId, r.GenomeId, r.Score, r.VerifierVersion, r.At, r.Signature });
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] insert worker_result failed: {e.Message}"); }
    }

    private readonly HashSet<int> _epochsSeen = new();
    public async Task UpsertEpochAsync(EpochRow r)
    {
        if (!Enabled) return;
        lock (_epochsSeen) { if (!_epochsSeen.Add(r.Epoch)) return; } // once per process is enough
        try
        {
            await Insert("epochs",
                new[] { "epoch", "core_version", "mining_seed", "first_tick", "last_tick", "hi_threshold", "add_threshold" },
                new object?[] { (ushort)r.Epoch, r.CoreVersion, (r.MiningSeed.Length == 64 ? r.MiningSeed : new string('0', 64)),
                    (uint)r.FirstTick, (uint)r.LastTick, (uint)r.HiThreshold, (uint)r.AddThreshold });
        }
        catch (Exception e) { lock (_epochsSeen) { _epochsSeen.Remove(r.Epoch); } Console.WriteLine($"[clickhouse] upsert epoch failed: {e.Message}"); }
    }

    private async Task<List<object?[]>> Query(string sql, int cols)
    {
        var rows = new List<object?[]>();
        await using var c = Conn();
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var r = new object?[cols];
            for (int i = 0; i < cols; i++) r[i] = rd.IsDBNull(i) ? null : rd.GetValue(i);
            rows.Add(r);
        }
        return rows;
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
    public async Task<List<(string Id, long Verifications, long Correct, DateTime FirstSeen, DateTime LastSeen)>?> VerifierLeaderboardAsync(int limit)
    {
        if (!Enabled) return null;
        try
        {
            var rows = await Query(
                $"SELECT wr.worker_id, count() AS n, countIf(wr.genome_id = v.genome_id) AS correct, min(wr.at), max(wr.at) " +
                $"FROM {_db}.worker_results wr " +
                $"LEFT JOIN (SELECT hash, argMax(genome_id, verified_at) AS genome_id FROM {_db}.verifications GROUP BY hash) v " +
                $"ON wr.hash = v.hash " +
                $"WHERE length(wr.worker_id) = 60 " +
                $"GROUP BY wr.worker_id ORDER BY correct DESC, n DESC LIMIT {Math.Clamp(limit, 1, 500)}", 5);
            return rows.Select(r => (S(r[0]).TrimEnd('\0'), L(r[1]), L(r[2]),
                Convert.ToDateTime(r[3]), Convert.ToDateTime(r[4]))).ToList();
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] verifier leaderboard failed: {e.Message}"); return null; }
    }

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

    public async Task<List<(string Hash, int Epoch, long Tick, string Algorithm, int Score, string Status, string Spark)>?> ComputorRecentAsync(string id, int limit)
    {
        if (!Enabled) return null;
        var cid = OnlyAZ(id); if (cid.Length != 60) return null;
        try
        {
            var rows = await Query(
                $"SELECT hash, epoch, tick, algorithm, score, status, spark FROM {_db}.verifications FINAL " +
                $"WHERE computor_id = '{cid}' ORDER BY tick DESC LIMIT {Math.Clamp(limit, 1, 200)}", 7);
            return rows.Select(r => (S(r[0]).TrimEnd('\0'), (int)L(r[1]), L(r[2]), S(r[3]), (int)L(r[4]), S(r[5]), S(r[6]))).ToList();
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] computor recent failed: {e.Message}"); return null; }
    }

    // Recent verified proofs (each has a spark) — powers the "Verified only" list view.
    public async Task<List<(string Hash, int Epoch, long Tick, string Algorithm, string ComputorId, int Score, int Confirmations, string Status, string Spark)>?>
        VerifiedProofsAsync(int limit, int offset, string? algo, int? epoch)
    {
        if (!Enabled) return null;
        try
        {
            var where = "status IN ('confirmed','conflicted')";
            if (!string.IsNullOrEmpty(algo) && (algo == "HyperIdentity" || algo == "Addition")) where += $" AND algorithm = '{algo}'";
            if (epoch.HasValue) where += $" AND epoch = {epoch.Value}";
            var rows = await Query(
                $"SELECT hash, epoch, tick, algorithm, computor_id, score, confirmations, status, spark " +
                $"FROM {_db}.verifications FINAL WHERE {where} ORDER BY tick DESC " +
                $"LIMIT {Math.Clamp(limit, 1, 100)} OFFSET {Math.Clamp(offset, 0, 100000)}", 9);
            return rows.Select(r => (S(r[0]).TrimEnd('\0'), (int)L(r[1]), L(r[2]), S(r[3]), S(r[4]).TrimEnd('\0'),
                (int)L(r[5]), (int)L(r[6]), S(r[7]), S(r[8]))).ToList();
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] verified proofs failed: {e.Message}"); return null; }
    }

    // epoch -> verified/confirmed/conflicted counts.
    public async Task<Dictionary<int, (long Verified, long Confirmed, long Conflicted)>?> EpochCountsAsync()
    {
        if (!Enabled) return null;
        try
        {
            var rows = await Query(
                $"SELECT epoch, count(), countIf(status='confirmed'), countIf(status='conflicted') " +
                $"FROM {_db}.verifications FINAL GROUP BY epoch", 4);
            return rows.ToDictionary(r => (int)L(r[0]), r => (L(r[1]), L(r[2]), L(r[3])));
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] epoch counts failed: {e.Message}"); return null; }
    }

    public sealed record Verdict(string Status, int Score, int Confirmations, string GenomeId, bool Passes, string Spark);

    public async Task<Dictionary<string, Verdict>?> VerdictsForAsync(IEnumerable<string> hashes)
    {
        if (!Enabled) return null;
        var list = hashes.Where(h => !string.IsNullOrEmpty(h)).Distinct().ToList();
        if (list.Count == 0) return new();
        try
        {
            var inClause = string.Join(",", list.Select(h => "'" + h.Replace("'", "") + "'"));
            var rows = await Query(
                $"SELECT hash, status, score, confirmations, genome_id, passes, spark FROM {_db}.verifications FINAL " +
                $"WHERE hash IN ({inClause})", 7);
            var map = new Dictionary<string, Verdict>();
            foreach (var r in rows)
                map[S(r[0]).TrimEnd('\0')] = new Verdict(S(r[1]), (int)L(r[2]), (int)L(r[3]), S(r[4]), L(r[5]) != 0, S(r[6]));
            return map;
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] verdicts failed: {e.Message}"); return null; }
    }

    public async Task<Verdict?> VerdictForAsync(string hash)
    {
        var m = await VerdictsForAsync(new[] { hash });
        return m != null && m.TryGetValue(hash, out var v) ? v : null;
    }

    public async Task<(long Verified, long Confirmed, long Conflicted, long Computors)?> VerificationStatsAsync()
    {
        if (!Enabled) return null;
        try
        {
            var rows = await Query(
                $"SELECT count(), countIf(status='confirmed'), countIf(status='conflicted'), uniqExact(computor_id) " +
                $"FROM {_db}.verifications FINAL", 4);
            if (rows.Count == 0) return (0, 0, 0, 0);
            var r = rows[0];
            return (L(r[0]), L(r[1]), L(r[2]), L(r[3]));
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] stats failed: {e.Message}"); return null; }
    }

    public async Task<(long Verifications, long WorkerResults, long Epochs)?> RowCountsAsync()
    {
        if (!Enabled) return null;
        try
        {
            var v = await Query($"SELECT count() FROM {_db}.verifications FINAL", 1);
            var w = await Query($"SELECT count() FROM {_db}.worker_results", 1);
            var e = await Query($"SELECT count() FROM {_db}.epochs FINAL", 1);
            return (L(v.FirstOrDefault()?[0]), L(w.FirstOrDefault()?[0]), L(e.FirstOrDefault()?[0]));
        }
        catch (Exception e) { Console.WriteLine($"[clickhouse] counts failed: {e.Message}"); return null; }
    }
}
