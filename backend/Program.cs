using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QubicAtlas;

var builder = WebApplication.CreateBuilder(args);

// ---- config (env, matching the Node reference) ----
string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
int EnvInt(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;

var port = EnvInt("PORT", 8099);
var rpcUrl = Env("QUBIC_RPC", "https://rpc.qubic.org");
var solutionDest = Env("SOLUTION_DEST", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFXIB");
var verifierPath = Env("VERIFIER", File.Exists("/usr/local/bin/verifier")
    ? "/usr/local/bin/verifier"
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scorer", "verifier")));
var cacheDir = Path.GetFullPath(Env("CACHE_DIR", "./.cache"));
var webDist = Path.GetFullPath(Env("WEB_DIST", Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "frontend", "dist")));
var requiredConfirmations = EnvInt("ATLAS_CONFIRMATIONS", 1);
var clickhouseUrl = Env("CLICKHOUSE_URL", "");           // unset => file/RPC fallback, no CH
var clickhouseDb = Env("CLICKHOUSE_DB", "atlas");
var clickhouseUser = Env("CLICKHOUSE_USER", "");         // override the URL-embedded user/pass
var clickhousePassword = Env("CLICKHOUSE_PASSWORD", "");
var verifierVersion = Env("ATLAS_VERIFIER_VERSION", "");  // "" => version pinning disabled
var minReputation = EnvInt("ATLAS_MIN_REPUTATION", -3);
// ---- worker authentication (signed submissions) ----
bool EnvBool(string k, bool d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
    ? v.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on"
    : d;
var requireSigned = EnvBool("ATLAS_REQUIRE_SIGNED", true);   // default: reject unsigned submissions
var workerAllowlist = WorkerAuth.LoadAllowlist(
    Environment.GetEnvironmentVariable("ATLAS_WORKER_ALLOWLIST"),
    Environment.GetEnvironmentVariable("ATLAS_WORKER_ALLOWLIST_FILE"));
var auth = new WorkerAuth(requireSigned, workerAllowlist);
Console.WriteLine($"[auth] requireSigned={requireSigned} mode={auth.Mode} allowlistSize={auth.AllowlistSize}");

builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ---- services ----
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var rpc = new QubicRpc(http, rpcUrl, solutionDest);
var epochMap = new EpochMap(rpc);
var index = new SolutionIndex(rpc, EnvInt("ATLAS_INDEX_CAP", 1000));
var verifier = new Verifier(verifierPath);
// Multi-epoch: trust BOTH the current build's version and the historical binaries' version,
// so verified historical (build0/1/2) results still count toward consensus.
var trustedVersions = string.IsNullOrEmpty(verifierVersion)
    ? ""
    : $"{verifierVersion},{Registry.HistoricalVerifierVersion}";
var queue = new JobQueue(requiredConfirmations, leaseMs: 180000,
    expectedVersion: trustedVersions, minReputation: minReputation);
var store = new ClickHouseStore(clickhouseUrl, clickhouseDb, clickhouseUser, clickhousePassword);

Directory.CreateDirectory(cacheDir);
// Start the HTTP server as fast as possible. Neither the firehose index nor ClickHouse is needed
// to serve /api/health, /api/live/tick-info or /api/verify, and blocking startup on either means a
// slow/rate-limited RPC or a briefly-unavailable ClickHouse delays (or crash-loops) the whole
// process → nginx 502 on every restart. So warm both in the background with retries instead.
_ = index.ReloadAsync();   // ReloadAsync swallows its own errors; never throws
_ = Task.Run(async () =>
{
    for (var attempt = 0; ; attempt++)
    {
        try { await store.InitAsync(); if (attempt > 0) Console.WriteLine("[startup] ClickHouse init ok (retry)"); break; }
        catch (Exception e)
        {
            Console.WriteLine($"[startup] ClickHouse init failed (attempt {attempt + 1}): {e.Message}");
            if (attempt >= 150) { Console.WriteLine("[startup] giving up on ClickHouse init retries"); break; }
            await Task.Delay(2000);
        }
    }
});

builder.Services.AddSingleton(rpc);
builder.Services.AddSingleton(epochMap);
builder.Services.AddSingleton(index);
builder.Services.AddSingleton(verifier);
builder.Services.AddSingleton(queue);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(auth);

var app = builder.Build();

// Refresh the recent-solution index from the firehose every 30s.
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(30000);
        try { await index.ReloadAsync(); } catch { }
    }
});

// ---- ClickHouse persistence helper: compute genome_id server-side and upsert our verdict ----
// Stores ONLY our result (never the raw proof or the 212KB reconstruction blob). Returns the
// server-computed genome_id so callers can reuse it. Best-effort: no-ops if CH is disabled.
// Parse a stored "n1,n2,..." spark into a JSON array for the client.
JsonArray? SparkArray(string s)
{
    if (string.IsNullOrEmpty(s)) return null;
    var arr = new JsonArray();
    foreach (var p in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        if (long.TryParse(p, out var n)) arr.Add(n);
    return arr.Count > 0 ? arr : null;
}

// Compact, downsampled best-score curve ("How the miner found it", mini) for the proofs list.
string SparkOf(JsonNode recon)
{
    var events = recon["mutationTrace"]?["events"]?.AsArray();
    if (events is null || events.Count == 0) return "";
    var best = new List<long>(events.Count);
    foreach (var e in events) best.Add(NodeLong(e?["bestScore"]) ?? 0);
    const int target = 48;
    if (best.Count <= target) return string.Join(",", best);
    var outp = new List<long>(target);
    for (int i = 0; i < target; i++) outp.Add(best[(int)((long)i * (best.Count - 1) / (target - 1))]);
    return string.Join(",", outp);
}

async Task<string> PersistVerification(JsonNode recon, string hash, string? computorId, long tick,
    int? epoch, string status, int confirmations, string? verifierVersion)
{
    var genomeId = GenomeId.Compute(recon);
    if (!store.Enabled) return genomeId;
    try
    {
        var o = recon.AsObject();
        string algorithm = o["algorithm"]?.GetValue<string>() ?? "";
        int score = (int)(NodeLong(o["score"]) ?? 0);
        string ver = verifierVersion ?? o["reconstructorVersion"]?.GetValue<string>() ?? "";
        int ep = epoch ?? (int)(NodeLong(o["epoch"]) ?? 0);
        // Per-epoch threshold is authoritative (a historical binary's own threshold is imprecise
        // across its version span); recompute passesThreshold = score >= threshold.
        long threshold = Registry.IsSupported(ep)
            ? Registry.ThresholdForEpoch(ep, algorithm)
            : (NodeLong(o["threshold"]) ?? Registry.Threshold.GetValueOrDefault(algorithm));
        bool passes = score >= threshold;
        var now = DateTime.UtcNow;

        await store.UpsertVerificationAsync(new ClickHouseStore.VerificationRow(
            Hash: hash, Epoch: ep, Tick: tick, Ts: now, Algorithm: algorithm,
            ComputorId: computorId ?? "", Score: score, Passes: passes, Threshold: threshold,
            GenomeId: genomeId, VerifierVersion: ver, Status: status,
            Confirmations: confirmations, Spark: SparkOf(recon), VerifiedAt: now));

        // Enrich the epochs table (best-effort, once per epoch): boundaries from /v1/status,
        // thresholds from the registry, mining_seed from the proof inputs when present.
        try
        {
            var bounds = await epochMap.BoundariesAsync();
            var b = bounds.FirstOrDefault(x => x.Epoch == ep);
            string miningSeed = o["inputs"]?["miningSeed"]?.GetValue<string>() ?? "";
            await store.UpsertEpochAsync(new ClickHouseStore.EpochRow(
                Epoch: ep,
                CoreVersion: o["coreVersion"]?.GetValue<string>() ?? Registry.P.CoreVersion,
                MiningSeed: miningSeed,
                FirstTick: b.First > 0 ? b.First : tick,
                LastTick: b.Last > 0 ? b.Last : tick,
                HiThreshold: ThresholdFor(ep, "HyperIdentity"),
                AddThreshold: ThresholdFor(ep, "Addition")));
        }
        catch { }
    }
    catch (Exception e) { Console.WriteLine($"[persist] {hash}: {e.Message}"); }
    return genomeId;
}

// ---- JSON helpers: serialize ourselves so shape/order matches JSON.stringify byte-for-byte ----
var jsonOpts = new JsonSerializerOptions
{
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    // Emit raw UTF-8 (e.g. the "—" em-dash) rather than \uXXXX, matching Node's JSON.stringify.
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
IResult J(object obj) => Results.Content(JsonSerializer.Serialize(obj, jsonOpts), "application/json");
IResult Err(int code, object obj) => Results.Content(JsonSerializer.Serialize(obj, jsonOpts), "application/json", null, code);

// timestamp is only emitted when the source tx carried it (JS omits `undefined`).
static JsonNode? TsNode(object? ts) => ts switch
{
    null => null,
    long l => JsonValue.Create(l),
    int i => JsonValue.Create((long)i),
    string str => JsonValue.Create(str),
    _ => JsonValue.Create(ts.ToString()),
};

// Per-epoch threshold for display/verdict. Uses the exact per-epoch value when the epoch is
// supported, otherwise falls back to the active param set's threshold.
int ThresholdFor(int? epoch, string algorithm) =>
    Registry.IsSupported(epoch)
        ? Registry.ThresholdForEpoch(epoch!.Value, algorithm)
        : Registry.Threshold.GetValueOrDefault(algorithm);

// Run the era-correct verifier binary for a proof's epoch and override threshold/passesThreshold
// with the exact per-epoch value (historical binaries carry an imprecise build-wide threshold).
// Returns the reconstruction JsonObject. Caller MUST have checked Registry.IsSupported(epoch).
async Task<JsonObject> VerifyForEpoch(int epoch, string miningSeed, string pubKey, string nonce)
{
    var bin = Registry.VerifierPathForEpoch(epoch, verifierPath);
    var recon = await verifier.RunAtAsync(bin, miningSeed, pubKey, nonce, "-1");
    var obj = recon.AsObject();
    string algorithm = obj["algorithm"]?.GetValue<string>() ?? "";
    long score = NodeLong(obj["score"]) ?? 0;
    int threshold = Registry.ThresholdForEpoch(epoch, algorithm);
    obj["threshold"] = threshold;
    obj["passesThreshold"] = score >= threshold;   // pass rule is score >= threshold for both algos
    return obj;
}

// Resolve a proof's epoch->build (null epoch or unsupported => build null; those are never leased
// to a capability-limited worker and never verified).
async Task<(int? Epoch, string? Build)> EpochBuildForTick(long tick)
{
    var epoch = await epochMap.EpochForTickAsync(tick);
    return (epoch, Registry.IsSupported(epoch) ? Registry.BuildForEpoch(epoch!.Value) : null);
}

// Sample a HISTORICAL epoch's proofs straight from the archive (tick-descending). The recent
// in-memory index only holds the current epoch, so selecting an older epoch would otherwise show
// nothing. Bounded scan + RPC caching keep paging responsive; barren stretches just return fewer.
async Task<(List<object> Items, bool HasMore)> SampleEpochProofsAsync(int epoch, int limit, int offset, string? algo)
{
    var bounds = await epochMap.BoundariesAsync();
    var e = bounds.FirstOrDefault(x => x.Epoch == epoch);
    var items = new List<object>();
    if (e.First == 0 && e.Last == 0) return (items, false);
    int need = offset + limit + 1;                 // +1 to detect hasMore
    int scanned = 0; const int scanCap = 250;
    long tick = e.Last;
    var collected = new List<Solution>();
    while (collected.Count < need && scanned < scanCap && tick >= e.First)
    {
        long t = tick; tick--; scanned++;
        List<Solution> sols;
        try { sols = await rpc.SolutionsInTick(t); } catch { continue; }
        foreach (var s in sols)
            if (string.IsNullOrEmpty(algo) || s.Algorithm == algo) collected.Add(s);
    }
    bool hasMore = collected.Count > offset + limit;
    foreach (var s in collected.Skip(offset).Take(limit)) items.Add(FirehoseProof(s, epoch));
    return (items, hasMore);
}

// Overlay our recomputed verdicts (from ClickHouse) onto each Proof's verification badge.
async Task OverlayVerdictsAsync(List<object> items)
{
    if (!store.Enabled) return;
    var hashes = items.OfType<JsonObject>()
        .Select(o => o["hash"]?.GetValue<string>() ?? "").Where(h => h.Length > 0).ToList();
    var verdicts = await store.VerdictsForAsync(hashes);
    if (verdicts is not { Count: > 0 }) return;
    foreach (var o in items.OfType<JsonObject>())
    {
        var h = o["hash"]?.GetValue<string>() ?? "";
        if (verdicts.TryGetValue(h, out var v))
            o["verification"] = new JsonObject
            {
                ["status"] = v.Status, ["score"] = v.Score,
                ["confirmations"] = v.Confirmations, ["spark"] = SparkArray(v.Spark),
            };
    }
}

// Build the enriched Proof object for the firehose path (full inputs present).
JsonObject FirehoseProof(Solution s, int? epoch)
{
    var o = new JsonObject
    {
        ["hash"] = s.Hash,
        ["algorithm"] = s.Algorithm,
        ["computorId"] = s.ComputorId,
        ["computorPublicKey"] = s.ComputorPublicKey,
        ["miningSeed"] = s.MiningSeed,
        ["nonce"] = s.Nonce,
        ["tickNumber"] = s.TickNumber,
    };
    if (s.Timestamp is not null) o["timestamp"] = TsNode(s.Timestamp);
    o["epoch"] = epoch;
    o["threshold"] = ThresholdFor(epoch, s.Algorithm);
    o["coreVersion"] = Registry.P.CoreVersion;
    o["verification"] = new JsonObject { ["status"] = "unverified" };
    return o;
}

// Build the enriched Proof object for the index path (index rows carry no seed/nonce/pubkey).
JsonObject IndexProof(IndexRow r, int? epoch)
{
    var o = new JsonObject
    {
        ["hash"] = r.Hash,
        ["algorithm"] = r.Algorithm,
        ["computorId"] = r.ComputorId,
        ["tickNumber"] = r.TickNumber,
    };
    if (r.Timestamp is not null) o["timestamp"] = TsNode(r.Timestamp);
    o["epoch"] = epoch;
    o["threshold"] = ThresholdFor(epoch, r.Algorithm);
    o["coreVersion"] = Registry.P.CoreVersion;
    o["verification"] = new JsonObject { ["status"] = "unverified" };
    return o;
}

// ---- live chain data ----
app.MapGet("/api/live/tick-info", async () =>
{
    try { return J(await rpc.TickInfo()); }
    catch (Exception e) { return Err(502, new { error = e.ToString() }); }
});

// ---- solutions from the public archive ----
app.MapGet("/api/solutions", async (HttpRequest req) =>
{
    try
    {
        int limit = Math.Min(50, ParseInt(req.Query["limit"], 25));
        int offset = ParseInt(req.Query["offset"], 0);
        string? algo = req.Query["algorithm"];
        string? computor = req.Query["computor"];
        int? epochQ = req.Query.ContainsKey("epoch") && int.TryParse(req.Query["epoch"], out var eq) ? eq : null;

        // "Verified only" view: confirmed proofs from ClickHouse — each carries a search spark.
        // This branch OWNS the verified filter: it must never fall through to the raw firehose (which
        // would show unverified proofs and look like the filter did nothing). If the verifications
        // store is unavailable, return an empty set with a clear source so the UI can say so.
        bool verifiedOnly = req.Query["verified"] == "1" || req.Query["verified"] == "true";
        if (verifiedOnly)
        {
            if (!store.Enabled)
                return J(new { items = Array.Empty<object>(), hasMore = false, source = "verifications-unavailable", indexed = index.Rows.Count });
            var vp = await store.VerifiedProofsAsync(limit, offset, algo, epochQ) ?? new();
            var vitems = vp.Select(r => (object)new JsonObject
            {
                ["hash"] = r.Hash, ["algorithm"] = r.Algorithm, ["epoch"] = r.Epoch,
                ["computorId"] = r.ComputorId, ["tickNumber"] = r.Tick,
                ["threshold"] = ThresholdFor(r.Epoch, r.Algorithm),
                ["coreVersion"] = Registry.P.CoreVersion,
                ["verification"] = new JsonObject
                {
                    ["status"] = r.Status, ["score"] = r.Score, ["confirmations"] = r.Confirmations,
                    ["spark"] = SparkArray(r.Spark),
                },
            }).ToList();
            return J(new { items = vitems, hasMore = vitems.Count >= limit, source = "verifications", indexed = index.Rows.Count });
        }

        // Historical epoch filter: the recent index/firehose only holds the CURRENT epoch, so an
        // older-epoch selection must be sampled directly from the archive (tick-descending).
        if (epochQ.HasValue && Registry.IsSupported(epochQ))
        {
            var bnds = await epochMap.BoundariesAsync();
            var current = bnds.Count > 0 ? bnds[^1].Epoch : (int?)null;
            if (epochQ != current)
            {
                var (epItems, epHasMore) = await SampleEpochProofsAsync(epochQ.Value, limit, offset, algo);
                await OverlayVerdictsAsync(epItems);
                return J(new { items = epItems, hasMore = epHasMore, source = "archive-epoch", indexed = index.Rows.Count });
            }
        }

        List<object> items;
        bool hasMore;
        string source;
        var rows = index.Rows;

        if (rows.Count > 0 && (!string.IsNullOrEmpty(algo) || !string.IsNullOrEmpty(computor) || epochQ.HasValue || offset > 0))
        {
            IEnumerable<IndexRow> filtered = rows;
            if (!string.IsNullOrEmpty(algo)) filtered = filtered.Where(r => r.Algorithm == algo);
            if (!string.IsNullOrEmpty(computor)) filtered = filtered.Where(r => r.ComputorId == computor);
            var filteredList = filtered.ToList();
            var page = new List<object>();
            foreach (var r in filteredList.Skip(offset).Take(limit + 200))
            {
                var epoch = await epochMap.EpochForTickAsync(r.TickNumber);
                if (epochQ.HasValue && epoch != epochQ) continue;
                page.Add(IndexProof(r, epoch));
                if (page.Count >= limit) break;
            }
            items = page;
            hasMore = offset + limit < filteredList.Count;
            source = "index";
        }
        else
        {
            var r = await rpc.ListSolutions(limit * (string.IsNullOrEmpty(algo) ? 1 : 3), offset);
            items = new List<object>();
            foreach (var s in r.Items)
            {
                var epoch = await epochMap.EpochForTickAsync(s.TickNumber);
                if (string.IsNullOrEmpty(algo) || algo == s.Algorithm)
                    items.Add(FirehoseProof(s, epoch));
            }
            items = items.Take(limit).ToList();
            hasMore = r.HasMore;
            source = "firehose";
        }
        // Overlay our recomputed verdicts onto each Proof's verification badge (from ClickHouse).
        if (store.Enabled)
        {
            var hashes = items.OfType<JsonObject>()
                .Select(o => o["hash"]?.GetValue<string>() ?? "").Where(h => h.Length > 0).ToList();
            var verdicts = await store.VerdictsForAsync(hashes);
            if (verdicts is { Count: > 0 })
                foreach (var o in items.OfType<JsonObject>())
                {
                    var h = o["hash"]?.GetValue<string>() ?? "";
                    if (verdicts.TryGetValue(h, out var v))
                        o["verification"] = new JsonObject
                        {
                            ["status"] = v.Status,
                            ["score"] = v.Score,
                            ["confirmations"] = v.Confirmations,
                            ["spark"] = SparkArray(v.Spark),
                        };
                }
        }
        return J(new { items, hasMore, source, indexed = index.Rows.Count });
    }
    catch (Exception e) { return Err(502, new { error = "rpc_failed", detail = e.ToString() }); }
});

// Browse solutions in a given tick.
app.MapGet("/api/ticks/{tick}/solutions", async (long tick) =>
{
    try
    {
        var sols = await rpc.SolutionsInTick(tick);
        var epoch = await epochMap.EpochForTickAsync(tick);
        var items = sols.Select(s => FirehoseProof(s, epoch)).ToList();
        return J(new { items, tick });
    }
    catch (Exception e) { return Err(502, new { error = "rpc_failed", detail = e.ToString() }); }
});

app.MapGet("/api/solutions/{hash}", async (string hash) =>
{
    try
    {
        var s = await rpc.GetSolution(hash);
        var epoch = await epochMap.EpochForTickAsync(s.TickNumber);
        var o = new JsonObject
        {
            ["hash"] = s.Hash,
            ["algorithm"] = s.Algorithm,
            ["computorId"] = s.ComputorId,
            ["computorPublicKey"] = s.ComputorPublicKey,
            ["miningSeed"] = s.MiningSeed,
            ["nonce"] = s.Nonce,
            ["tickNumber"] = s.TickNumber,
        };
        if (s.Timestamp is not null) o["timestamp"] = TsNode(s.Timestamp);
        o["epoch"] = epoch;
        o["threshold"] = ThresholdFor(epoch, s.Algorithm);
        o["coreVersion"] = Registry.P.CoreVersion;
        o["scoreRule"] = s.Algorithm == "Addition" ? "maximum" : "minimum";
        var verdict = await store.VerdictForAsync(Sanitize(hash));
        if (verdict is not null)
        {
            o["annGenomeId"] = verdict.GenomeId;
            o["verification"] = new JsonObject
            {
                ["status"] = verdict.Status,
                ["score"] = verdict.Score,
                ["passesThreshold"] = verdict.Passes,
                ["confirmations"] = verdict.Confirmations,
            };
        }
        else
        {
            o["annGenomeId"] = "(compute by verifying)";
            o["verification"] = new JsonObject { ["status"] = "unverified" };
        }
        return J(o);
    }
    catch (Exception e) { return Err(404, new { error = "not_found", detail = e.ToString() }); }
});

// ---- epochs (derived from /v1/status + registry) ----
app.MapGet("/api/epochs", async () =>
{
    try
    {
        var b = (await epochMap.BoundariesAsync()).AsEnumerable().Reverse().ToList();
        var counts = await store.EpochCountsAsync();   // epoch -> (verified, confirmed, conflicted)
        var items = b
            .Where(e => Registry.IsSupported(e.Epoch))   // only epochs a bundled verifier reproduces
            .Select(e =>
            {
                (long Verified, long Confirmed, long Conflicted) c = default;
                bool has = counts is not null && counts.TryGetValue(e.Epoch, out c);
                return new
                {
                    epoch = e.Epoch,
                    coreVersion = Registry.CoreVersionForEpoch(e.Epoch),
                    algoFamily = Registry.P.AlgoFamily,
                    firstTick = e.First,
                    lastTick = e.Last,
                    // proofs WE have verified in this epoch (we don't index every on-chain proof) —
                    // and how many reached network consensus.
                    verified = has ? (object)c.Verified : 0,
                    confirmed = has ? (object)c.Confirmed : 0,
                    conflicted = has ? (object)c.Conflicted : 0,
                };
            }).ToList();
        return J(new { items, minSupportedEpoch = Registry.MinSupportedEpoch });
    }
    catch (Exception e) { return Err(502, new { error = e.ToString() }); }
});

// ---- verifier contributor leaderboard (per signed identity; each is a payable Qubic address) ----
app.MapGet("/api/verifiers/leaderboard", async (int? limit) =>
{
    try
    {
        var lb = await store.VerifierLeaderboardAsync(limit ?? 25);
        if (lb is null) return J(new { items = Array.Empty<object>(), source = "unavailable" });
        var items = lb.Select((r, i) => new
        {
            rank = i + 1,
            id = r.Id,
            verifications = r.Verifications,
            correct = r.Correct,
            firstSeen = r.FirstSeen.ToString("o"),
            lastSeen = r.LastSeen.ToString("o"),
        }).ToList();
        return J(new { items, total = items.Count, source = "clickhouse" });
    }
    catch (Exception e) { return Results.Json(new { items = Array.Empty<object>(), error = e.Message }); }
});

// ---- single computor overview: stats + recent proofs (from the indexed window) ----
app.MapGet("/api/computors/{id}", async (string id) =>
{
    try
    {
        // Prefer ClickHouse (our verified proofs) — that's where the leaderboard data lives.
        var summary = await store.ComputorSummaryAsync(id);
        if (summary is { } s && s.Total > 0)
        {
            var rows = await store.ComputorRecentAsync(id, 25) ?? new();
            var recent = rows.Select(r => (object)new JsonObject
            {
                ["hash"] = r.Hash, ["algorithm"] = r.Algorithm, ["epoch"] = r.Epoch,
                ["computorId"] = id, ["tickNumber"] = r.Tick, ["threshold"] = ThresholdFor(r.Epoch, r.Algorithm),
                ["coreVersion"] = Registry.P.CoreVersion,
                ["verification"] = new JsonObject { ["status"] = r.Status, ["score"] = r.Score, ["spark"] = SparkArray(r.Spark) },
            }).ToList();
            var epochs = rows.Select(r => r.Epoch).Where(e => e > 0).Distinct().OrderBy(e => e).ToList();
            return J(new
            {
                computorId = id, proofs = s.Total, verified = s.Total,
                epochs, firstTick = s.FirstTick, lastTick = s.LastTick,
                algorithms = new { HyperIdentity = s.Hi, Addition = s.Add },
                recent, source = "clickhouse",
            });
        }

        // Fallback: the indexed window (if CH is empty/disabled).
        var mine = index.Rows.Where(r => r.ComputorId == id).ToList();
        var epochsIdx = new SortedSet<int>();
        long firstTick = long.MaxValue, lastTick = 0;
        foreach (var r in mine)
        {
            var e = await epochMap.EpochForTickAsync(r.TickNumber);
            if (e.HasValue) epochsIdx.Add(e.Value);
            firstTick = Math.Min(firstTick, r.TickNumber); lastTick = Math.Max(lastTick, r.TickNumber);
        }
        var recentIdx = new List<object>();
        foreach (var r in mine.OrderByDescending(r => r.TickNumber).Take(25))
            recentIdx.Add(IndexProof(r, await epochMap.EpochForTickAsync(r.TickNumber)));
        return J(new
        {
            computorId = id, proofs = mine.Count, verified = 0,
            epochs = epochsIdx.ToList(), firstTick = firstTick == long.MaxValue ? 0 : firstTick, lastTick,
            algorithms = mine.GroupBy(r => r.Algorithm).ToDictionary(g => g.Key, g => g.Count()),
            recent = recentIdx, source = "index",
        });
    }
    catch (Exception e) { return Results.Json(new { computorId = id, error = e.Message }); }
});

// ---- computors: leaderboard aggregated from the index (or recent window fallback) ----
app.MapGet("/api/computors", async () =>
{
    try
    {
        // Prefer ClickHouse (our recomputed verifications) when it has rows.
        var lb = await store.LeaderboardAsync();
        if (lb is { Count: > 0 })
        {
            var chRows = lb.Select((r, i) => new
            {
                computorId = r.ComputorId,
                solutions = r.Solutions,
                firstTick = r.FirstTick,
                lastTick = r.LastTick,
                rank = i + 1,
            }).ToList();
            return J(new { items = chRows, total = chRows.Count, window = chRows.Count, source = "clickhouse" });
        }

        bool fromIndex = index.Rows.Count > 0;
        int window;
        var agg = new Dictionary<string, (string computorId, int solutions, long firstTick, long lastTick)>();

        void Add(string? computorId, long tick)
        {
            var id = computorId ?? "";
            if (agg.TryGetValue(id, out var a))
                agg[id] = (a.computorId, a.solutions + 1, Math.Min(a.firstTick, tick), Math.Max(a.lastTick, tick));
            else
                agg[id] = (id, 1, tick, tick);
        }

        if (fromIndex)
        {
            foreach (var r in index.Rows) Add(r.ComputorId, r.TickNumber);
            window = index.Rows.Count;
        }
        else
        {
            var sample = (await rpc.ListSolutions(600, 0)).Items;
            foreach (var s in sample) Add(s.ComputorId, s.TickNumber);
            window = sample.Count;
        }

        var rows = agg.Values
            .OrderByDescending(x => x.solutions)
            .Select((r, i) => new
            {
                computorId = r.computorId,
                solutions = r.solutions,
                firstTick = r.firstTick,
                lastTick = r.lastTick,
                rank = i + 1,
            }).ToList();

        return J(new { items = rows, total = rows.Count, window, source = fromIndex ? "index" : "recent-window" });
    }
    catch (Exception e) { return Err(502, new { error = e.ToString() }); }
});

app.MapGet("/api/index/status", async () =>
{
    var rows = index.Rows;
    var ch = await store.RowCountsAsync();
    return J(new
    {
        indexedSolutions = rows.Count,
        newestTick = rows.Count > 0 ? rows[0].TickNumber : (long?)null,
        oldestTick = rows.Count > 0 ? rows[^1].TickNumber : (long?)null,
        clickhouse = store.Enabled
            ? (object)new
            {
                enabled = true,
                verifications = ch?.Verifications ?? 0,
                workerResults = ch?.WorkerResults ?? 0,
                epochs = ch?.Epochs ?? 0,
            }
            : new { enabled = false },
    });
});

// Totals across our recomputed verdicts, sourced from ClickHouse.
app.MapGet("/api/verifications/stats", async () =>
{
    var s = await store.VerificationStatsAsync();
    if (s is null)
        return J(new { source = store.Enabled ? "clickhouse" : "disabled", verified = 0, confirmed = 0, conflicted = 0, computors = 0 });
    var v = s.Value;
    return J(new { source = "clickhouse", verified = v.Verified, confirmed = v.Confirmed, conflicted = v.Conflicted, computors = v.Computors });
});

// ---- THE decentralized bit: reconstruct + verify locally from public inputs ----
app.MapGet("/api/verify/{hash}", async (string hash, HttpRequest req) =>
{
    var clean = Sanitize(hash);
    var cacheFile = Path.Combine(cacheDir, $"{clean}.json");
    if (File.Exists(cacheFile) && !req.Query.ContainsKey("nocache"))
    {
        var cached = await File.ReadAllTextAsync(cacheFile);
        req.HttpContext.Response.Headers["x-annx-cache"] = "hit";
        // Check ClickHouse first for a cached verdict; backfill a row from the on-disk cache
        // so aggregations (leaderboard/epochs/stats) are populated even for pre-existing caches.
        try
        {
            var node = JsonNode.Parse(cached);
            if (node is not null)
            {
                var verdict = await store.VerdictForAsync(clean);
                req.HttpContext.Response.Headers["x-atlas-verdict"] = verdict?.Status ?? "none";
                if (verdict is null)
                {
                    var co = node.AsObject();
                    await PersistVerification(node, clean,
                        co["computorId"]?.GetValue<string>(),
                        NodeLong(co["tickNumber"]) ?? 0,
                        (int?)NodeLong(co["epoch"]),
                        "confirmed", 1, co["reconstructorVersion"]?.GetValue<string>());
                }
            }
        }
        catch { }
        return Results.Content(cached, "application/json");
    }
    try
    {
        var sol = await rpc.GetSolution(clean);
        var epoch = await epochMap.EpochForTickAsync(sol.TickNumber);
        var (supported, paramSetId) = Registry.ParamSetForEpoch(epoch);
        // Multi-epoch guard: only run a binary for an epoch a bundled era-binary reproduces.
        if (!Registry.IsSupported(epoch))
            return Err(422, new
            {
                error = "epoch_unsupported",
                epoch,
                minSupportedEpoch = Registry.MinSupportedEpoch,
                detail = epoch is null
                    ? "could not resolve epoch for tick"
                    : $"epoch {epoch} is not reproducible by any bundled verifier (supported: {Registry.MinSupportedEpoch}–222)",
            });
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Dispatch to the era-correct binary and apply the exact per-epoch threshold.
        var obj = await VerifyForEpoch(epoch!.Value, sol.MiningSeed, sol.ComputorPublicKey, sol.Nonce);
        obj["elapsedMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t0;
        obj["solutionHash"] = clean;
        obj["epoch"] = epoch;
        obj["coreVersion"] = Registry.P.CoreVersion;
        obj["paramSetId"] = paramSetId;
        obj["paramSetSupported"] = supported;
        obj["computorId"] = sol.ComputorId;
        obj["tickNumber"] = sol.TickNumber;
        obj["inputs"] = new JsonObject
        {
            ["miningSeed"] = sol.MiningSeed,
            ["computorPublicKey"] = sol.ComputorPublicKey,
            ["nonce"] = sol.Nonce,
        };
        obj["source"] = "public-archive";
        obj["verifiedLocally"] = true;
        obj["relayClaimedScore"] = -1;
        var outStr = JsonSerializer.Serialize(obj, jsonOpts);
        await File.WriteAllTextAsync(cacheFile, outStr);
        req.HttpContext.Response.Headers["x-annx-cache"] = "miss";
        // Our own run is the referee: upsert a confirmed verdict (confirmations = 1).
        await PersistVerification(obj, clean, sol.ComputorId, sol.TickNumber, epoch,
            "confirmed", 1, obj["reconstructorVersion"]?.GetValue<string>());
        return Results.Content(outStr, "application/json");
    }
    catch (Exception e) { return Err(500, new { error = "verify_failed", detail = e.Message }); }
});

app.MapGet("/api/health", async () =>
{
    // ClickHouse status is the usual "why is verified/epochs data empty?" answer, so surface it:
    // enabled=false -> CLICKHOUSE_URL unset; enabled=true & ok=false -> unreachable/auth failure;
    // ok=true with verifications=0 -> connected but not yet populated (workers still confirming).
    object ch;
    if (!store.Enabled) ch = new { enabled = false };
    else
    {
        var rc = await store.RowCountsAsync();
        ch = rc is null
            ? new { enabled = true, ok = false }
            : new { enabled = true, ok = true, verifications = rc.Value.Verifications,
                    workerResults = rc.Value.WorkerResults, epochs = rc.Value.Epochs };
    }
    return J(new
    {
        ok = true,
        rpc = rpcUrl,
        solutionDest = rpc.SolutionDest,
        verifier = verifier.Exists,
        relay = "none (public archive only)",
        rpcCache = rpc.CacheStats(),
        clickhouse = ch,
    });
});

// ================= Distributed verifier network (job API) =================

app.MapPost("/api/jobs/enqueue", async (HttpRequest req) =>
{
    var body = await ReadJson(req);
    var hash = Sanitize(body?["hash"]?.GetValue<string>() ?? "");
    if (string.IsNullOrEmpty(hash)) return Err(400, new { error = "missing_hash" });
    // Resolve the proof's epoch/build so the job is capability-taggable; reject unsupported epochs.
    int? epoch = null; string? build = null;
    try
    {
        var sol = await rpc.GetSolution(hash);
        (epoch, build) = await EpochBuildForTick(sol.TickNumber);
        if (!Registry.IsSupported(epoch))
            return Err(422, new { error = "epoch_unsupported", epoch });
    }
    catch (Exception e) { return Err(502, new { error = "rpc_failed", detail = e.Message }); }
    return J(JobJson(queue.Enqueue(hash, epoch, build)));
});

// Enqueue N recent unverified proofs from the firehose.
app.MapPost("/api/jobs/seed", async (HttpRequest req) =>
{
    int n = ParseInt(req.Query["n"], 5);
    try
    {
        var r = await rpc.ListSolutions(n, 0);
        var seeded = new List<object>();
        foreach (var s in r.Items)
        {
            if (string.IsNullOrEmpty(s.Hash)) continue;
            var (epoch, build) = await EpochBuildForTick(s.TickNumber);
            if (!Registry.IsSupported(epoch)) continue;   // never enqueue an unsupported epoch
            seeded.Add(JobJson(queue.Enqueue(s.Hash, epoch, build, sol: s)));
        }
        return J(new { seeded, count = seeded.Count });
    }
    catch (Exception e) { return Err(502, new { error = "rpc_failed", detail = e.ToString() }); }
});

app.MapGet("/api/jobs/claim", async (HttpRequest req) =>
{
    var worker = req.Query["worker"].ToString();
    if (string.IsNullOrEmpty(worker)) worker = "anon";
    // Capability match: the worker reports which era-binaries it has (?builds=build0,build3).
    // If omitted, we don't restrict (legacy workers assumed to have the current build only... but
    // to stay backward-compatible we treat "no builds reported" as no capability filter).
    var buildsRaw = req.Query["builds"].ToString();
    var supportedBuilds = string.IsNullOrEmpty(buildsRaw)
        ? null
        : buildsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .ToHashSet();
    var job = queue.Claim(worker, supportedBuilds);
    if (job is null) return Results.StatusCode(204);
    try
    {
        // Fast path: inputs were captured at enqueue time (firehose/tick read) — serve with NO RPC.
        // This is the overwhelmingly common case and is what keeps claim off the archive under load.
        string algorithm, miningSeed, pubKey, nonce;
        long? tick = job.TickNumber;
        if (job.MiningSeed is not null && job.ComputorPublicKey is not null && job.Nonce is not null)
        {
            miningSeed = job.MiningSeed; pubKey = job.ComputorPublicKey; nonce = job.Nonce;
            algorithm = job.Algorithm ?? "";
        }
        else
        {
            // Fallback: a hash-only job (e.g. POST /api/jobs/enqueue). Fetch once (cached + coalesced).
            var sol = await rpc.GetSolution(job.Hash);
            miningSeed = sol.MiningSeed; pubKey = sol.ComputorPublicKey; nonce = sol.Nonce;
            algorithm = sol.Algorithm; tick = sol.TickNumber;
        }
        // Resolve the proof's epoch/build so the worker runs the right binary (boundaries are cached).
        var epoch = job.Epoch ?? (tick is not null ? await epochMap.EpochForTickAsync(tick.Value) : null);
        var build = job.Build ?? (Registry.IsSupported(epoch) ? Registry.BuildForEpoch(epoch!.Value) : null);
        return J(new
        {
            jobId = job.Id,
            hash = job.Hash,
            algorithm,
            miningSeed,
            computorPublicKey = pubKey,
            nonce,
            epoch,
            build,
            threshold = ThresholdFor(epoch, algorithm),
        });
    }
    catch (Exception e)
    {
        // A transient RPC failure while enriching a hash-only job must NOT surface as a 502 — that
        // spams workers and looks like an outage. Release the lease and tell the worker "nothing
        // right now" (204) so it quietly polls again; the job is immediately re-claimable.
        queue.Release(job.Id);
        Console.WriteLine($"[claim] enrich failed for {job.Hash}: {e.Message}");
        return Results.StatusCode(204);
    }
});

app.MapPost("/api/jobs/{id}/result", async (string id, HttpRequest req) =>
{
    var body = await ReadJson(req);
    var claimedWorker = body?["worker"]?.GetValue<string>();
    var pubKeyHex = body?["publicKey"]?.GetValue<string>();
    var sigHex = body?["signature"]?.GetValue<string>();
    var recon = body?["reconstruction"];
    if (recon is null) return Err(400, new { error = "missing_reconstruction" });

    // Need the TRUSTED job (its hash) to recompute the signed message; a client can't be trusted
    // to restate the hash it's signing over.
    var jobForAuth = queue.Get(id);
    if (jobForAuth is null) return Err(409, new { error = "unknown_job" });

    // The genome_id is computed SERVER-SIDE from the submitted reconstruction — a worker's own
    // annGenomeId claim is never trusted. Honest workers hash to the same id; a divergent id is
    // what surfaces a conflict.
    long? score = NodeLong(recon["score"]);
    long? threshold = NodeLong(recon["threshold"]);
    bool? passes = recon["passesThreshold"]?.GetValue<bool>();
    string? algorithm = recon["algorithm"]?.GetValue<string>();
    string? verVer = recon["reconstructorVersion"]?.GetValue<string>();
    string genomeId = GenomeId.Compute(recon);

    // ---- authenticate: verify the worker's signature over the canonical message ----
    // message = "{jobId}|{hash}|{genomeId}|{score}" (deterministic + reproducible here).
    var canonical = WorkerAuth.CanonicalMessage(id, jobForAuth.Hash, genomeId, score);
    var authRes = auth.Check(claimedWorker, pubKeyHex, sigHex, canonical);
    if (!authRes.Ok)
    {
        Console.WriteLine($"[auth] job {id}: REJECTED worker='{claimedWorker}' reason={authRes.Error}");
        return Err(authRes.Code, new { error = authRes.Error });
    }
    // Bind reputation + audit to the VERIFIED identity, not the self-asserted string.
    var worker = authRes.Identity;

    var outcome = queue.Submit(id, worker, score, passes, genomeId, verVer, recon.DeepClone());
    if (outcome.Error is not null) return Err(409, new { error = outcome.Error });
    var job = outcome.Job!;

    // Audit trail: every worker submission is recorded (per proof, per verified identity),
    // including the signature so the row is independently attributable + non-repudiable.
    await store.InsertWorkerResultAsync(new ClickHouseStore.WorkerResultRow(
        Hash: job.Hash, WorkerId: worker, GenomeId: genomeId, Score: (int)(score ?? 0),
        VerifierVersion: verVer ?? "", At: DateTime.UtcNow, Signature: sigHex ?? ""));

    async Task WriteCache(JsonNode reconNode, string source, bool overwrite)
    {
        try
        {
            var clean0 = Sanitize(job.Hash);
            var cacheFile0 = Path.Combine(cacheDir, $"{clean0}.json");
            if (File.Exists(cacheFile0) && !overwrite) return;
            var o = reconNode.AsObject();
            if (o["solutionHash"] is null) o["solutionHash"] = clean0;
            o["source"] = source;
            if (o["verifiedLocally"] is null) o["verifiedLocally"] = true;
            await File.WriteAllTextAsync(cacheFile0, o.ToJsonString());
        }
        catch { }
    }

    if (outcome.State == JobQueue.ConsensusState.Pending)
        return J(new { done = false, status = "pending", confirmations = job.Confirmations });

    // Non-pending: we need the proof metadata (computor/tick/epoch) to persist our verdict.
    Solution sol;
    try { sol = await rpc.GetSolution(job.Hash); }
    catch (Exception e) { return Err(502, new { error = "rpc_failed", detail = e.Message }); }
    var epoch = await epochMap.EpochForTickAsync(sol.TickNumber);

    if (outcome.State == JobQueue.ConsensusState.Confirmed)
    {
        var fin = queue.Finalize(id, outcome.WinningGenomeId!, outcome.WinningScore, passes,
            threshold, algorithm, resolvedByReferee: false);
        await PersistVerification(job.Reconstruction ?? recon, job.Hash, sol.ComputorId,
            sol.TickNumber, epoch, "confirmed", fin?.Confirmations ?? job.Confirmations, verVer);
        if (job.Reconstruction is not null) await WriteCache(job.Reconstruction, "distributed-network", overwrite: false);
        return J(new { done = true, status = "confirmed", confirmations = fin?.Confirmations ?? job.Confirmations,
            genomeId = outcome.WinningGenomeId, verifiedScore = outcome.WinningScore, resolvedByReferee = false });
    }

    // Conflicted: workers disagree on the genome. The SERVER REFEREE (its own trusted verifier
    // recompute) is authoritative. Whoever matches it is correct; dissenters are flagged and
    // their reputation is decremented (handled in Finalize). Determinism makes the verdict
    // independently re-derivable — trust comes from reproducibility, not from trusting a worker.
    if (!Registry.IsSupported(epoch))
        return Err(422, new { error = "epoch_unsupported", epoch });
    JsonObject refObj;
    try { refObj = await VerifyForEpoch(epoch!.Value, sol.MiningSeed, sol.ComputorPublicKey, sol.Nonce); }
    catch (Exception e) { return Err(500, new { error = "referee_failed", detail = e.Message }); }
    JsonNode refRecon = refObj;
    string refGenome = GenomeId.Compute(refRecon);
    long? refScore = NodeLong(refObj["score"]);
    bool? refPasses = refObj["passesThreshold"]?.GetValue<bool>();
    long? refThreshold = NodeLong(refObj["threshold"]);
    string? refAlgorithm = refObj["algorithm"]?.GetValue<string>();
    string? refVersion = refObj["reconstructorVersion"]?.GetValue<string>();

    // Enrich the referee reconstruction so the cached blob matches /api/verify shape.
    refObj["solutionHash"] = Sanitize(job.Hash);
    refObj["epoch"] = epoch;
    refObj["coreVersion"] = Registry.P.CoreVersion;
    refObj["computorId"] = sol.ComputorId;
    refObj["tickNumber"] = sol.TickNumber;
    refObj["inputs"] = new JsonObject
    {
        ["miningSeed"] = sol.MiningSeed,
        ["computorPublicKey"] = sol.ComputorPublicKey,
        ["nonce"] = sol.Nonce,
    };

    var finC = queue.Finalize(id, refGenome, refScore, refPasses, refThreshold, refAlgorithm,
        resolvedByReferee: true);

    // Record the referee as an audit row too (worker_id = "referee"; server recompute, unsigned).
    await store.InsertWorkerResultAsync(new ClickHouseStore.WorkerResultRow(
        Hash: job.Hash, WorkerId: "referee", GenomeId: refGenome, Score: (int)(refScore ?? 0),
        VerifierVersion: refVersion ?? "", At: DateTime.UtcNow, Signature: ""));

    await PersistVerification(refRecon, job.Hash, sol.ComputorId, sol.TickNumber, epoch,
        "conflicted", finC?.Confirmations ?? 0, refVersion);
    await WriteCache(refRecon, "referee-resolved", overwrite: true);

    var dissenters = (finC?.Results ?? new List<JobQueue.JobResult>())
        .Where(r => r.Eligible && r.GenomeId != refGenome)
        .Select(r => new { worker = r.Worker, genomeId = r.GenomeId, score = r.Score })
        .ToList();

    Console.WriteLine($"[consensus] job {id} CONFLICT resolved by referee: authoritative score={refScore} " +
                      $"genome={refGenome[..12]}… matched={finC?.Confirmations} dissenters={dissenters.Count}");

    return J(new
    {
        done = true,
        status = "conflicted",
        resolvedByReferee = true,
        confirmations = finC?.Confirmations ?? 0,
        verifiedScore = refScore,
        genomeId = refGenome,
        dissenters,
    });
});

app.MapGet("/api/jobs/stats", () =>
{
    // Surface the auth mode alongside the queue stats (authMode: "open"|"allowlist").
    var node = JsonSerializer.SerializeToNode(queue.Stats(), jsonOpts)!.AsObject();
    node["authMode"] = auth.Mode;
    node["requireSigned"] = auth.RequireSigned;
    node["allowlistSize"] = auth.AllowlistSize;
    return J(node);
});
app.MapGet("/api/jobs/recent", () => J(queue.Recent()));

// ================= static SPA =================
if (Directory.Exists(webDist))
{
    var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    var opts = new Microsoft.AspNetCore.Builder.StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webDist),
        ContentTypeProvider = provider,
        OnPrepareResponse = ctx =>
        {
            var p = ctx.File.Name;
            // index.html must never be cached, or the browser keeps pointing at a stale
            // (deleted) bundle hash and white-screens. Hashed assets are safe forever.
            if (p.EndsWith(".html"))
                ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            else if (ctx.Context.Request.Path.Value?.Contains("/assets/") == true)
                ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        }
    };
    app.UseDefaultFiles(new Microsoft.AspNetCore.Builder.DefaultFilesOptions
    { FileProvider = opts.FileProvider });
    app.UseStaticFiles(opts);

    // SPA fallback for non-API routes.
    app.MapFallback(async (HttpContext ctx) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Response.ContentType = "text/html";
        await ctx.Response.SendFileAsync(Path.Combine(webDist, "index.html"));
    });
}

// ---- background: continuously feed recent proofs to the verifier network ----
// Without this the workers idle after startup, the leaderboard freezes, and proofs
// stay "unverified". Enqueue dedups active hashes; we skip proofs already confirmed in CH.
{
    bool autoEnqueue = (Environment.GetEnvironmentVariable("ATLAS_AUTO_ENQUEUE") ?? "true") != "false";
    int intervalMs = int.TryParse(Environment.GetEnvironmentVariable("ATLAS_AUTO_ENQUEUE_INTERVAL_MS"), out var ei) ? ei : 15000;
    int batch = int.TryParse(Environment.GetEnvironmentVariable("ATLAS_AUTO_ENQUEUE_BATCH"), out var eb) ? eb : 12;
    bool backfill = (Environment.GetEnvironmentVariable("ATLAS_BACKFILL") ?? "true") != "false";
    int backfillBatch = int.TryParse(Environment.GetEnvironmentVariable("ATLAS_BACKFILL_BATCH"), out var bb) ? bb : 24;
    int backfillIntervalMs = int.TryParse(Environment.GetEnvironmentVariable("ATLAS_BACKFILL_INTERVAL_MS"), out var bi) ? bi : 4000;
    const int backfillTickScanCap = 300;   // ticks probed per cycle (bounds per-cycle RPC)
    int backfillFetchThrottleMs = int.TryParse(Environment.GetEnvironmentVariable("ATLAS_BACKFILL_THROTTLE_MS"), out var bt) ? bt : 100;  // ~10 RPC/s ceiling for backfill
    long? backfillStartTick = long.TryParse(Environment.GetEnvironmentVariable("ATLAS_BACKFILL_START_TICK"), out var bst) ? bst : null;
    long? backfillCursor = backfillStartTick;   // persistent-ish in-memory cursor, walks DOWN
    long backfillFloor = 0;

    // ---- LOOP A: current-epoch proofs (HIGH priority) — always fed first ----
    if (autoEnqueue)
    {
        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    var recent = (await rpc.ListSolutions(batch * 2, 0)).Items;
                    if (recent.Count == 0) continue;
                    var verdicts = store.Enabled
                        ? await store.VerdictsForAsync(recent.Select(s => s.Hash).ToList()) : null;
                    int added = 0;
                    foreach (var s in recent)
                    {
                        if (added >= batch) break;
                        if (verdicts != null && verdicts.ContainsKey(s.Hash)) continue;
                        var (epoch, build) = await EpochBuildForTick(s.TickNumber);
                        if (!Registry.IsSupported(epoch)) continue;
                        queue.Enqueue(s.Hash, epoch, build, sol: s);   // high priority, inputs captured
                        added++;
                    }
                    if (added > 0) Console.WriteLine($"[auto-enqueue] +{added} current-epoch proofs queued");
                }
                catch (Exception e) { Console.WriteLine($"[auto-enqueue] {e.Message}"); }
            }
        });
        Console.WriteLine($"[auto-enqueue] on — every {intervalMs}ms, up to {batch} proofs/cycle");
    }

    // ---- LOOP B: historical backfill (LOW priority) — fills spare capacity, tick desc ----
    // Enqueued LOW, so the queue serves current-epoch work first: latest is never starved.
    // Its own faster cadence + a capacity gate keep idle workers busy without unbounded growth.
    if (backfill)
    {
        _ = Task.Run(async () =>
        {
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(backfillIntervalMs));
            while (await timer.WaitForNextTickAsync())
            {
                try
                {
                    var (pending, leased) = queue.Load();
                    if (pending + leased >= backfillBatch * 2) continue;   // enough buffered — let it drain

                    var bounds = await epochMap.BoundariesAsync();
                    if (bounds.Count == 0) continue;
                    var supBounds = bounds.Where(b => Registry.IsSupported(b.Epoch)).ToList();
                    if (supBounds.Count == 0) continue;
                    backfillFloor = supBounds.Min(b => b.First);
                    long currentEpochFirst = bounds[^1].First;
                    if (backfillCursor is null || backfillCursor > currentEpochFirst - 1)
                        backfillCursor = currentEpochFirst - 1;
                    if (backfillCursor < backfillFloor)
                    {
                        Console.WriteLine($"[backfill] history exhausted (floor tick {backfillFloor})");
                        continue;
                    }

                    int addedH = 0, scanned = 0;
                    long tick = backfillCursor.Value;
                    int? lastEp = null; long lastTick = tick;
                    while (addedH < backfillBatch && scanned < backfillTickScanCap && tick >= backfillFloor)
                    {
                        long t = tick; tick--; scanned++;
                        int? ep = null;
                        foreach (var b in bounds) if (t >= b.First && t <= b.Last) { ep = b.Epoch; break; }
                        if (!Registry.IsSupported(ep)) continue;
                        // Throttle: at most ~1 tick fetch per backfillFetchThrottleMs so this background
                        // scan can't burst hundreds of requests at the public RPC and get us rate-limited
                        // (which would cascade into failures for the live path too).
                        await Task.Delay(backfillFetchThrottleMs);
                        List<Solution> sols;
                        try { sols = await rpc.SolutionsInTick(t); } catch { continue; }
                        if (sols.Count == 0) continue;
                        var verdicts = store.Enabled
                            ? await store.VerdictsForAsync(sols.Where(s => s.Hash != null).Select(s => s.Hash!).ToList()) : null;
                        foreach (var s in sols)
                        {
                            if (addedH >= backfillBatch) break;
                            if (string.IsNullOrEmpty(s.Hash)) continue;
                            if (verdicts != null && verdicts.ContainsKey(s.Hash)) continue;
                            queue.Enqueue(s.Hash, ep, Registry.BuildForEpoch(ep!.Value), low: true, sol: s);   // LOW priority, inputs captured
                            addedH++; lastEp = ep; lastTick = t;
                        }
                    }
                    backfillCursor = tick;
                    if (addedH > 0)
                        Console.WriteLine($"[backfill] +{addedH} historical proofs (tick~{lastTick}, epoch {lastEp}); cursor->{backfillCursor}");
                }
                catch (Exception e) { Console.WriteLine($"[backfill] {e.Message}"); }
            }
        });
        Console.WriteLine($"[backfill] on — every {backfillIntervalMs}ms, up to {backfillBatch} proofs/cycle (low priority)");
    }
}

Console.WriteLine($"Qubic Atlas API on http://localhost:{port}  (rpc={rpcUrl}, archive-only, verifier={verifier.Exists})");
app.Run();

// ---- local helpers ----
static int ParseInt(Microsoft.Extensions.Primitives.StringValues v, int d)
    => int.TryParse(v.ToString(), out var r) ? r : d;

static string Sanitize(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) sb.Append(c);
    return sb.ToString();
}

static async Task<JsonNode?> ReadJson(HttpRequest req)
{
    using var reader = new StreamReader(req.Body);
    var text = await reader.ReadToEndAsync();
    return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
}

static long? NodeLong(JsonNode? n)
{
    if (n is null) return null;
    return n.GetValueKind() switch
    {
        JsonValueKind.Number => n.GetValue<long>(),
        JsonValueKind.String => long.TryParse(n.GetValue<string>(), out var v) ? v : null,
        _ => null
    };
}

static object JobJson(JobQueue.Job j) => new
{
    id = j.Id,
    hash = j.Hash,
    status = j.Status,
    results = j.Results.Select(r => new { worker = r.Worker, score = r.Score, passes = r.Passes, genomeId = r.GenomeId, at = r.At }).ToList(),
    confirmations = j.Confirmations,
    createdAt = j.CreatedAt,
    leasedTo = j.LeasedTo,
    leaseExpiry = j.LeaseExpiry,
    verifiedScore = j.VerifiedScore,
    agreed = j.Agreed,
};
