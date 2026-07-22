using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qubic.Crypto;

namespace QubicAtlas;

// A mining solution decoded from an on-chain transaction.
public sealed class Solution
{
    public string? Hash { get; set; }
    public string Algorithm { get; set; } = "";
    public string? ComputorId { get; set; }
    public string ComputorPublicKey { get; set; } = "";
    public string MiningSeed { get; set; } = "";
    public string Nonce { get; set; } = "";
    public long TickNumber { get; set; }
    public object? Timestamp { get; set; }
}

// Port of server/rpc.js — ingestion from the PUBLIC Qubic RPC/Archive only.
//
// ANN mining solutions are on-chain transactions:
//   destination = SOLUTION_DEST, inputType = 2, inputSize = 64,
//   input(base64) = miningSeed(32) || nonce(32),  source = computor identity.
public sealed class QubicRpc
{
    public const int SolutionInputType = 2;
    public const int SolutionInputSize = 64;

    private readonly HttpClient _http;
    private readonly string _rpc;
    private static readonly IQubicCrypt Crypt = new QubicCrypt();

    public string SolutionDest { get; }

    public QubicRpc(HttpClient http, string rpc, string solutionDest)
    {
        _http = http;
        _rpc = rpc.TrimEnd('/');
        SolutionDest = solutionDest;
    }

    // Qubic identity (60 uppercase letters) -> 32-byte public key (hex).
    // Uses Qubic.Crypto (QubicCrypt.GetPublicKeyFromIdentity, a default interface member).
    // Falls back to base26 decode (4x8-byte little-endian frags, 14 chars each) on any error.
    public static string IdentityToPubkeyHex(string identity)
    {
        try
        {
            byte[] pk = Crypt.GetPublicKeyFromIdentity(identity);
            return Convert.ToHexString(pk).ToLowerInvariant();
        }
        catch
        {
            // Fallback: manual base26 decode (matches rpc.js identityToPubkeyHex).
            var pk = new byte[32];
            for (int i = 0; i < 4; i++)
            {
                ulong frag = 0;
                for (int jj = 13; jj >= 0; jj--)
                    frag = frag * 26UL + (ulong)(identity[i * 14 + jj] - 'A');
                for (int b = 0; b < 8; b++)
                    pk[i * 8 + b] = (byte)((frag >> (8 * b)) & 0xff);
            }
            return Convert.ToHexString(pk).ToLowerInvariant();
        }
    }

    private static string B64Hex(string b64) => Convert.ToHexString(Convert.FromBase64String(b64)).ToLowerInvariant();

    // ---- in-memory GET cache (per-URL TTL) + single-flight + stale-on-error ----
    // The archive is the hot dependency. Most GETs are either effectively immutable (a confirmed
    // transaction, a past tick's transactions) or polled far faster than they change (tick-info).
    // Caching collapses duplicate upstream calls; single-flight ensures one request per URL even
    // under a burst of concurrent callers; stale-on-error keeps serving the last good value when
    // the RPC hiccups, so a transient upstream failure doesn't cascade into a user-facing error.
    private readonly ConcurrentDictionary<string, (long ExpMs, JsonNode Node)> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<JsonNode>>> _inflight = new();
    private long _hits, _misses, _coalesced, _staleServed;

    public object CacheStats() => new
    {
        entries = _cache.Count,
        hits = Interlocked.Read(ref _hits),
        misses = Interlocked.Read(ref _misses),
        coalesced = Interlocked.Read(ref _coalesced),
        staleServed = Interlocked.Read(ref _staleServed),
    };

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // Per-endpoint freshness. Immutable data gets a long TTL; the chain tip a short one.
    private static int TtlMs(string url)
    {
        if (url.Contains("/v2/transactions/")) return 3_600_000;                          // confirmed tx: immutable
        if (url.Contains("/v1/ticks/") && url.Contains("/transactions")) return 60_000;   // past ticks: immutable
        if (url.Contains("/tick-info")) return 1_000;                                     // advances ~1/s; collapse polls
        if (url.Contains("/status")) return 5_000;                                        // epoch boundaries drift slowly
        if (url.Contains("/identities/") && url.Contains("/transactions")) return 5_000;  // firehose pages
        return 2_000;
    }

    private async Task<JsonNode> J(string url)
    {
        if (_cache.TryGetValue(url, out var hit) && hit.ExpMs > NowMs())
        {
            Interlocked.Increment(ref _hits);
            return hit.Node.DeepClone()!;   // clone so callers can reparent/mutate without corrupting the cache
        }
        Interlocked.Increment(ref _misses);

        // single-flight: concurrent callers for the same URL share one upstream request
        var mine = new Lazy<Task<JsonNode>>(() => Fetch(url));
        var lazy = _inflight.GetOrAdd(url, mine);
        if (!ReferenceEquals(lazy, mine)) Interlocked.Increment(ref _coalesced);
        try
        {
            var node = await lazy.Value;
            _cache[url] = (NowMs() + TtlMs(url), node);
            if (_cache.Count > 4096) Prune();
            return node.DeepClone()!;
        }
        catch
        {
            // Upstream failed — serve the last good value if we have one (correct for immutable data,
            // acceptably stale for the tip) rather than surfacing the error.
            if (_cache.TryGetValue(url, out var stale))
            {
                Interlocked.Increment(ref _staleServed);
                return stale.Node.DeepClone()!;
            }
            throw;
        }
        finally
        {
            if (ReferenceEquals(lazy, mine)) _inflight.TryRemove(url, out _);
        }
    }

    private void Prune()
    {
        var now = NowMs();
        foreach (var kv in _cache)
            if (kv.Value.ExpMs <= now) _cache.TryRemove(kv.Key, out _);
    }

    private async Task<JsonNode> Fetch(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("user-agent", "qubic-atlas/1.0");
        var r = await _http.SendAsync(req);
        if (!r.IsSuccessStatusCode)
            throw new Exception($"{(int)r.StatusCode} {url}");
        var s = await r.Content.ReadAsStringAsync();
        return JsonNode.Parse(s) ?? throw new Exception($"empty response {url}");
    }

    private static string? Str(JsonNode? n) => n is null ? null : (n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToString());

    // Map an archive transaction (any shape) into a solution record, or null if not a solution.
    private static Solution? ToSolution(JsonNode? tx)
    {
        if (tx is null) return null;
        JsonNode t = tx["transaction"] ?? tx;

        long inputType = ToLong(t["inputType"]);
        long inputSize = ToLong(t["inputSize"]);
        if (inputType != SolutionInputType || inputSize != SolutionInputSize) return null;

        string? hex = Str(t["inputHex"]);
        if (string.IsNullOrEmpty(hex))
        {
            var input = Str(t["input"]);
            hex = string.IsNullOrEmpty(input) ? null : B64Hex(input);
        }
        if (hex is null || hex.Length < 128) return null;

        string miningSeed = hex.Substring(0, 64);
        string nonce = hex.Substring(64, 64);
        string algorithm = (Convert.ToInt32(nonce.Substring(0, 2), 16) % 2 == 0) ? "HyperIdentity" : "Addition";
        string? sourceId = Str(t["sourceId"]);

        return new Solution
        {
            Hash = Str(t["txId"]) ?? Str(t["hash"]),
            Algorithm = algorithm,
            ComputorId = sourceId,
            ComputorPublicKey = sourceId is null ? "" : IdentityToPubkeyHex(sourceId),
            MiningSeed = miningSeed,
            Nonce = nonce,
            // timestamp sits on the OUTER wrapper for by-hash (/v2/transactions/{id}) but inline for
            // flat firehose items — check both so the proof detail page gets a "when" too.
            TickNumber = ToLong(t["tickNumber"]),
            Timestamp = TimestampNode(tx["timestamp"] ?? t["timestamp"]),
        };
    }

    private static long ToLong(JsonNode? n)
    {
        if (n is null) return 0;
        return n.GetValueKind() switch
        {
            JsonValueKind.Number => n.GetValue<long>(),
            JsonValueKind.String => long.TryParse(n.GetValue<string>(), out var v) ? v : 0,
            _ => 0
        };
    }

    private static object? TimestampNode(JsonNode? n)
    {
        if (n is null) return null;
        return n.GetValueKind() switch
        {
            JsonValueKind.Number => n.GetValue<long>(),
            JsonValueKind.String => n.GetValue<string>(),
            _ => n.ToString()
        };
    }

    public sealed record ListResult(List<Solution> Items, bool HasMore);

    // Firehose: list recent solutions from the archive (newest first).
    public async Task<ListResult> ListSolutions(int limit = 25, int offset = 0)
    {
        var outList = new List<Solution>();
        int page = (offset / 100) + 1;
        int startAt = offset % 100;
        int guard = 0;
        while (outList.Count < limit + startAt && guard++ < 40)
        {
            var d = await J($"{_rpc}/v2/identities/{SolutionDest}/transactions?desc=true&page={page}");
            var txs = d["transactions"] as JsonArray;
            if (txs is null || txs.Count == 0) break;
            foreach (var tx in txs)
            {
                var s = ToSolution(tx);
                if (s is not null) outList.Add(s);
            }
            var totalPages = d["pagination"]?["totalPages"];
            if (totalPages is not null && page >= ToLong(totalPages)) break;
            page++;
        }
        var items = outList.Skip(startAt).Take(limit).ToList();
        return new ListResult(items, outList.Count > startAt + limit);
    }

    // Solutions in a specific tick — reaches historical solutions older than the firehose window.
    public async Task<List<Solution>> SolutionsInTick(long tick)
    {
        var d = await J($"{_rpc}/v1/ticks/{tick}/transactions");
        var txs = d["transactions"] as JsonArray;
        var outList = new List<Solution>();
        if (txs is not null)
            foreach (var tx in txs)
            {
                var s = ToSolution(tx);
                if (s is not null) outList.Add(s);
            }
        return outList;
    }

    // Fetch a single solution's public inputs by transaction hash.
    public async Task<Solution> GetSolution(string hash)
    {
        var d = await J($"{_rpc}/v2/transactions/{Uri.EscapeDataString(hash)}");
        var s = ToSolution(d);   // pass the full wrapper so the outer `timestamp` is captured
        if (s is null) throw new Exception("not_a_solution_tx");
        return s;
    }

    // Current epoch/tick from RPC (returns raw JSON of /v1/tick-info).
    public async Task<JsonNode> TickInfo() => await J($"{_rpc}/v1/tick-info");

    // Raw /v1/status (used to build the tick->epoch boundary map).
    public async Task<JsonNode> Status() => await J($"{_rpc}/v1/status");
}
