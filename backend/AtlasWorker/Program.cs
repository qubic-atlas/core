using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qubic.Crypto;
using QubicAtlas;   // GenomeId (linked from ../GenomeId.cs) — same canonical hash the server uses

// Qubic Atlas distributed verifier worker.
// Loops: GET {ATLAS_URL}/api/jobs/claim?worker=ID  -> run verifier on the returned inputs
//        -> POST {ATLAS_URL}/api/jobs/{id}/result { worker, publicKey, signature, reconstruction }.
// Reuses the same native C++ verifier binary the API shells out to.
//
// Worker identity is a persistent Qubic keypair (FourQ / SchnorrQ). The 55-char seed is loaded
// from (or generated into) ATLAS_KEY_FILE, and the derived 60-char Qubic identity becomes the
// worker id — so reputation and the server-side audit trail key on the identity. Each submission
// is signed over the canonical message "{jobId}|{hash}|{genomeId}|{score}".

static string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
static int EnvInt(string k, int d) => int.TryParse(Environment.GetEnvironmentVariable(k), out var v) ? v : d;
static bool EnvBool(string k, bool d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v
    ? v.Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on" : d;

var atlasUrl = Env("ATLAS_URL", "https://qubic-atlas.org").TrimEnd('/');
var verifierPath = Env("VERIFIER", File.Exists("/usr/local/bin/verifier") ? "/usr/local/bin/verifier" : "./verifier");
var pollMs = EnvInt("POLL_INTERVAL_MS", 3000);
var once = args.Contains("--once") || Env("RUN_ONCE", "") == "1";

// ---- multi-epoch capability: which era-binaries this worker can run ----
// build3 => the VERIFIER path; build0/1/2 => sibling "verifier-<build>" in the same directory.
// The build->path map is either declared via ATLAS_BUILDS (e.g. "build0,build1,build2,build3") or
// probed from disk. The worker advertises these on claim so the server never hands it a job it
// can't run, and dispatches each job to the matching binary.
string BuildPath(string build)
{
    if (build == "build3") return verifierPath;
    var dir = System.IO.Path.GetDirectoryName(verifierPath);
    return string.IsNullOrEmpty(dir) ? $"verifier-{build}" : System.IO.Path.Combine(dir, $"verifier-{build}");
}
var buildPaths = new Dictionary<string, string>();
var declared = Env("ATLAS_BUILDS", "");
var candidates = string.IsNullOrEmpty(declared)
    ? new[] { "build0", "build1", "build2", "build3" }
    : declared.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
foreach (var b in candidates)
{
    var p = BuildPath(b);
    if (File.Exists(p)) buildPaths[b] = p;
    else if (!string.IsNullOrEmpty(declared))
        Console.WriteLine($"[atlas-worker] WARNING: declared build {b} missing binary at {p}");
}
if (buildPaths.Count == 0 && File.Exists(verifierPath)) buildPaths["build3"] = verifierPath; // legacy fallback
var buildsParam = string.Join(",", buildPaths.Keys.OrderBy(k => k));
Console.WriteLine($"[atlas-worker] builds={buildsParam}");

// ---- worker identity: persistent Qubic keypair ----
var signEnabled = EnvBool("ATLAS_SIGN", true);
// The {host} token is substituted with the machine name so multiple replicas sharing one volume
// still get distinct, persistent identities (N-of-M consensus needs DISTINCT workers).
var keyFile = Env("ATLAS_KEY_FILE", "/data/worker.key").Replace("{host}", Environment.MachineName);
IQubicCrypt crypt = new QubicCrypt();
string? keySeed = null;
byte[]? pubKey = null;
string workerId;
if (signEnabled)
{
    keySeed = ResolveSeed(keyFile);
    workerId = crypt.GetIdentityFromSeed(keySeed);   // Qubic identity == worker id (reputation key)
    pubKey = crypt.GetPublicKey(keySeed);
    Console.WriteLine($"[atlas-worker] identity={workerId} keyfile={keyFile}");
}
else
{
    workerId = Env("WORKER_ID", $"worker-{Environment.MachineName}-{Environment.ProcessId}");
    Console.WriteLine("[atlas-worker] WARNING: signing DISABLED (ATLAS_SIGN=false) — submissions are unsigned");
}

var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
// Keep a warm verifier process per build: it caches the ~512MB seed pool and only regenerates it
// when the miningSeed changes, so same-seed proofs skip the ~610ms pool-gen (~94% of compute).
// Falls back to one-shot spawns automatically for any binary that doesn't support --serve.
var serve = EnvBool("WORKER_SERVE", true);
var daemons = new ConcurrentDictionary<string, VerifierDaemon>();
Console.WriteLine($"[atlas-worker] id={workerId} atlas={atlasUrl} verifier={verifierPath} signed={signEnabled} serve={serve} once={once}");

static bool ValidSeed(string s) => s.Length == 55 && s.All(c => c >= 'a' && c <= 'z');

// Seed precedence: ATLAS_SEED (explicit, e.g. your wallet identity) -> ATLAS_KEY_FILE
// (mounted Docker secret / persisted file) -> auto-generate (zero-config default).
string ResolveSeed(string path)
{
    var provided = Environment.GetEnvironmentVariable("ATLAS_SEED")?.Trim();
    if (!string.IsNullOrEmpty(provided))
    {
        if (ValidSeed(provided))
        {
            // fail loud on a bad seed rather than silently minting a burner identity
            Console.WriteLine("[atlas-worker] using seed from ATLAS_SEED (bring-your-own identity)");
            return provided;
        }
        Console.Error.WriteLine("[atlas-worker] FATAL: ATLAS_SEED must be exactly 55 lowercase letters (a-z)");
        Environment.Exit(2);
    }
    return LoadOrCreateSeed(path);
}

// Load an existing 55-char a-z seed, or generate + persist a fresh one.
string LoadOrCreateSeed(string path)
{
    try
    {
        if (File.Exists(path))
        {
            var s = File.ReadAllText(path).Trim();
            if (s.Length == 55 && s.All(c => c >= 'a' && c <= 'z')) return s;
            Console.WriteLine($"[atlas-worker] key file {path} malformed — regenerating");
        }
    }
    catch (Exception e) { Console.WriteLine($"[atlas-worker] could not read key file {path}: {e.Message}"); }

    var seed = NewSeed();
    try
    {
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, seed);
        Console.WriteLine($"[atlas-worker] generated new worker key -> {path}");
    }
    catch (Exception e) { Console.WriteLine($"[atlas-worker] WARNING: could not persist key to {path}: {e.Message} (ephemeral identity)"); }
    return seed;
}

static string NewSeed()
{
    const string alphabet = "abcdefghijklmnopqrstuvwxyz";
    var bytes = RandomNumberGenerator.GetBytes(55);
    var sb = new StringBuilder(55);
    foreach (var b in bytes) sb.Append(alphabet[b % 26]);
    return sb.ToString();
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

async Task<JsonNode?> RunVerifier(string binaryPath, string seed, string pk, string nonce)
{
    var psi = new ProcessStartInfo
    {
        FileName = binaryPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    psi.ArgumentList.Add(seed);
    psi.ArgumentList.Add(pk);
    psi.ArgumentList.Add(nonce);
    psi.ArgumentList.Add("-1");
    using var proc = new Process { StartInfo = psi };
    var stdout = new StringBuilder();
    var stderr = new StringBuilder();
    proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
    proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
    proc.Start();
    proc.BeginOutputReadLine();
    proc.BeginErrorReadLine();
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    await proc.WaitForExitAsync(cts.Token);
    if (proc.ExitCode != 0)
        throw new Exception($"verifier exit {proc.ExitCode}: {stderr.ToString().Trim()}");
    return JsonNode.Parse(stdout.ToString().Trim());
}

async Task<bool> Tick()
{
    JsonNode? claim;
    try
    {
        var resp = await http.GetAsync(
            $"{atlasUrl}/api/jobs/claim?worker={Uri.EscapeDataString(workerId)}&builds={Uri.EscapeDataString(buildsParam)}");
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent) return false; // nothing to do
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"[atlas-worker] claim failed: {(int)resp.StatusCode}");
            return false;
        }
        claim = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }
    catch (Exception e) { Console.WriteLine($"[atlas-worker] claim error: {e.Message}"); return false; }

    if (claim is null) return false;
    var jobId = claim["jobId"]?.GetValue<string>();
    var hash = claim["hash"]?.GetValue<string>();
    var seed = claim["miningSeed"]?.GetValue<string>();
    var pk = claim["computorPublicKey"]?.GetValue<string>();
    var nonce = claim["nonce"]?.GetValue<string>();
    if (jobId is null || hash is null || seed is null || pk is null || nonce is null)
    {
        Console.WriteLine("[atlas-worker] malformed claim payload");
        return false;
    }

    // Dispatch to the era-binary the server tagged this proof with. If we lack it (shouldn't happen
    // given capability matching), skip without submitting so the job stays claimable by others.
    var build = claim["build"]?.GetValue<string>() ?? "build3";
    if (!buildPaths.TryGetValue(build, out var binaryPath))
    {
        Console.WriteLine($"[atlas-worker] skip {jobId}: no binary for build {build} (epoch {claim["epoch"]})");
        return false;
    }

    Console.WriteLine($"[atlas-worker] claimed {jobId} ({claim["algorithm"]}) epoch={claim["epoch"]} build={build} hash={claim["hash"]}");
    try
    {
        JsonNode? recon;
        if (serve)
        {
            var daemon = daemons.GetOrAdd(build, _ => new VerifierDaemon(binaryPath));
            recon = await daemon.Run(seed, pk, nonce, 120000);
            // A binary lacking --serve is detected once, then this build always one-shots.
            if (daemon.Unsupported) recon = await RunVerifier(binaryPath, seed, pk, nonce);
        }
        else recon = await RunVerifier(binaryPath, seed, pk, nonce);

        if (recon is null) { Console.WriteLine($"[atlas-worker] no result for {jobId} — leaving claimable"); return false; }
        if (recon["error"] is not null) { Console.WriteLine($"[atlas-worker] verifier error for {jobId}: {recon["error"]}"); return false; }

        var payload = new JsonObject { ["worker"] = workerId };

        if (signEnabled && keySeed is not null && recon is not null)
        {
            // Sign the SAME canonical message the server recomputes:
            //   "{jobId}|{hash}|{genomeId}|{score}"
            // genomeId is computed the identical canonical way (linked GenomeId.cs); score is the
            // reconstruction's score. This binds the signature to THIS job + THIS result.
            var genomeId = GenomeId.Compute(recon);
            var score = NodeLong(recon["score"]);
            var message = $"{jobId}|{hash}|{genomeId}|{(score?.ToString() ?? "")}";
            var sig = crypt.SignRaw(keySeed, Encoding.UTF8.GetBytes(message));
            payload["publicKey"] = Convert.ToHexString(pubKey!).ToLowerInvariant();
            payload["signature"] = Convert.ToHexString(sig).ToLowerInvariant();
        }

        payload["reconstruction"] = recon?.DeepClone();
        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var r = await http.PostAsync($"{atlasUrl}/api/jobs/{jobId}/result", content);
        var txt = await r.Content.ReadAsStringAsync();
        Console.WriteLine($"[atlas-worker] submitted {jobId} score={recon?["score"]} ({(int)r.StatusCode}) -> {txt}");
        return true;
    }
    catch (Exception e) { Console.WriteLine($"[atlas-worker] verify/submit error: {e.Message}"); return false; }
}

if (once)
{
    var did = await Tick();
    Environment.Exit(did ? 0 : 3); // 3 = nothing claimed
}

while (true)
{
    var did = await Tick();
    if (!did) await Task.Delay(pollMs);
}

// A long-lived verifier process in --serve mode. It holds the ~512MB seed pool in memory and only
// rebuilds it when the miningSeed changes — so a run of same-seed proofs pays the ~610ms pool-gen
// once instead of per proof. One instance per build (build0..build3); requests are serialized.
// If the binary doesn't support --serve (exits immediately), Unsupported latches true and the
// caller permanently falls back to one-shot spawns for that build.
sealed class VerifierDaemon
{
    readonly string _path;
    readonly SemaphoreSlim _gate = new(1, 1);
    Process? _proc;
    public bool Unsupported { get; private set; }

    public VerifierDaemon(string path) { _path = path; }

    public async Task<JsonNode?> Run(string seed, string pk, string nonce, int timeoutMs)
    {
        if (Unsupported) return null;   // caller one-shots instead
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_proc is null || _proc.HasExited)
                {
                    if (!Start()) { Unsupported = true; return null; }
                }
                try
                {
                    await _proc!.StandardInput.WriteLineAsync($"{seed} {pk} {nonce} -1");
                    await _proc.StandardInput.FlushAsync();
                    using var cts = new CancellationTokenSource(timeoutMs);
                    var sb = new StringBuilder();
                    while (true)
                    {
                        var line = await _proc.StandardOutput.ReadLineAsync(cts.Token);
                        if (line is null) throw new IOException("verifier daemon closed stdout");
                        if (line == "__ATLAS_EOF__") return JsonNode.Parse(sb.ToString().Trim());
                        sb.Append(line).Append('\n');
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[atlas-worker] daemon {System.IO.Path.GetFileName(_path)} error: {e.Message} (attempt {attempt + 1})");
                    try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
                    _proc = null;   // restart on next attempt; a second immediate failure gives up (→ null)
                }
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    bool Start()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _path,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--serve");
            var p = Process.Start(psi);
            if (p is null) return false;
            _proc = p;
            // Drain stderr so it can't fill the pipe and wedge the child; surface its lines.
            _ = Task.Run(async () =>
            {
                try { string? l; while ((l = await p.StandardError.ReadLineAsync()) is not null) Console.WriteLine($"[verifier:{System.IO.Path.GetFileName(_path)}] {l}"); }
                catch { }
            });
            // A binary without --serve prints usage and exits immediately; detect that here.
            Thread.Sleep(60);
            if (p.HasExited)
            {
                Console.WriteLine($"[atlas-worker] {System.IO.Path.GetFileName(_path)} exited on --serve (code {p.ExitCode}) — no serve support, using one-shot");
                _proc = null;
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[atlas-worker] cannot start daemon {_path}: {e.Message}");
            return false;
        }
    }
}
