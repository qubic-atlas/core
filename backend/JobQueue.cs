using System.Text;
using System.Text.Json.Nodes;

namespace QubicAtlas;

// In-memory check-job queue for the distributed verifier network.
//
// Consensus is CONFLICT-AWARE and grouped on the server-computed genome_id (a content hash
// of the trained network), not on the raw score. Because the verifier is deterministic there
// is exactly ONE correct (score, genome) per proof, so:
//   * confirmed  — RequiredConfirmations workers agree on the same genome_id;
//   * conflicted — workers submit DIFFERENT genome_ids -> the server referee (its own trusted
//                  verifier recompute) is authoritative, dissenters are flagged.
// Per-worker agreed/disagreed reputation is tracked; workers that repeatedly disagree with the
// referee/majority fall below a reputation floor and are no longer leased new jobs. Results
// from an unexpected verifier_version are recorded but do NOT count toward confirmation.
public sealed class JobQueue
{
    public sealed class JobResult
    {
        public string Worker { get; set; } = "";
        public long? Score { get; set; }
        public bool? Passes { get; set; }
        public string? GenomeId { get; set; }
        public string? VerifierVersion { get; set; }
        public bool Eligible { get; set; }        // expected version + non-null genome_id
        public long At { get; set; }
    }

    public sealed class Job
    {
        public string Id { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Status { get; set; } = "pending"; // pending | leased | resolving | done
        public List<JobResult> Results { get; } = new();
        public int Confirmations { get; set; }
        public long CreatedAt { get; set; }
        public string? LeasedTo { get; set; }
        public long LeaseExpiry { get; set; }
        public long? VerifiedScore { get; set; }
        public bool Agreed { get; set; }
        public bool ResolvedByReferee { get; set; }
        public long? Threshold { get; set; }
        public bool? Passes { get; set; }
        public string? Algorithm { get; set; }
        public string? GenomeId { get; set; }
        // Multi-epoch dispatch: which era this proof belongs to and which binary reproduces it.
        // Build null => not yet resolved (any worker may claim); otherwise capability-matched.
        public int? Epoch { get; set; }
        public string? Build { get; set; }
        // Low = historical backfill work. It's served ONLY when no current-epoch (high) job is
        // pending, so the latest proofs are never starved by history.
        public bool Low { get; set; }
        // Proof inputs captured at enqueue time (from the firehose/tick read) so a worker can be
        // served WITHOUT the API making a per-claim RPC round-trip. Null only for jobs enqueued by
        // hash alone (e.g. POST /api/jobs/enqueue) — those fall back to an RPC fetch on claim.
        public string? MiningSeed { get; set; }
        public string? ComputorPublicKey { get; set; }
        public string? ComputorId { get; set; }
        public string? Nonce { get; set; }
        public long? TickNumber { get; set; }
        public object? Timestamp { get; set; }   // proof's on-chain time (ms), for "when" display
        // The last full reconstruction submitted (written to the verify cache on done).
        public JsonNode? Reconstruction { get; set; }
    }

    private sealed class WorkerInfo
    {
        public long LastSeen;
        public int Completed;
        public int Agreed;
        public int Disagreed;
        public int Reputation => Agreed - Disagreed;
    }

    private readonly Dictionary<string, Job> _jobs = new();
    private readonly Dictionary<string, string> _byHash = new();
    // Jobs that already have ≥1 result and just need another DISTINCT worker to reach consensus.
    // Served before any fresh work so a proof's N confirmations happen back-to-back instead of the
    // partially-done job sinking to the back of the queue behind every newly-enqueued proof.
    private readonly Queue<string> _pendingCorroborate = new();
    private readonly Queue<string> _pending = new();      // current-epoch, not yet started (high)
    private readonly Queue<string> _pendingLow = new();   // historical backfill (low priority)
    private readonly Dictionary<string, WorkerInfo> _workers = new();
    // Done jobs are kept briefly (status polling / Recent()) then evicted so _jobs stays bounded.
    // Without this, every finalized job — with its ~200KB reconstruction — accumulated forever.
    private readonly Queue<string> _doneOrder = new();
    private const int MaxDoneRetained = 1000;
    private int _seq;
    private readonly object _lock = new();

    public int RequiredConfirmations { get; }
    // The set of trusted reconstructorVersions. Multi-epoch: the current build AND the historical
    // (qubic-atlas-hist-1) build are both trusted, so pass a comma-separated list.
    private readonly HashSet<string> _expectedVersions;
    public string ExpectedVersion => string.Join(",", _expectedVersions);
    private readonly int _minReputation;
    private readonly long _leaseMs;

    // expectedVersion == "" disables version pinning (every non-null genome counts); otherwise a
    // comma-separated list of trusted reconstructorVersions — results from any other build are
    // recorded but do NOT count toward confirmation.
    public JobQueue(int requiredConfirmations = 1, long leaseMs = 120000,
        string expectedVersion = "", int minReputation = -3)
    {
        RequiredConfirmations = Math.Max(1, requiredConfirmations);
        _expectedVersions = (expectedVersion ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet();
        _minReputation = minReputation;
        _leaseMs = leaseMs;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string Base36(long value)
    {
        if (value == 0) return "0";
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder();
        while (value > 0) { sb.Insert(0, digits[(int)(value % 36)]); value /= 36; }
        return sb.ToString();
    }

    private void Requeue(Job job) => (job.Low ? _pendingLow : _pending).Enqueue(job.Id);

    // Retire a job to the bounded done-set: drop its heavy reconstruction blob immediately (it's
    // already persisted to ClickHouse + the verify cache), then evict the oldest done jobs so the
    // in-memory dictionary can't grow without bound as millions of proofs are verified.
    private void RetireDone(Job job)
    {
        job.Reconstruction = null;
        _doneOrder.Enqueue(job.Id);
        while (_doneOrder.Count > MaxDoneRetained)
        {
            var oldId = _doneOrder.Dequeue();
            if (_jobs.TryGetValue(oldId, out var oj) && oj.Status == "done") _jobs.Remove(oldId);
        }
    }

    // Re-queue a job to the right lane: one that already has a partial result goes to the
    // corroboration lane (served first) so it reaches consensus quickly; a fresh job goes to its
    // normal high/low lane. Used everywhere a job returns to the queue after being handed out.
    private void RequeueSmart(Job job)
    {
        if (job.Results.Count > 0) _pendingCorroborate.Enqueue(job.Id);
        else Requeue(job);
    }

    // Store the proof inputs on a job so claim can serve it without an RPC fetch.
    private static void StoreInputs(Job j, Solution s)
    {
        j.MiningSeed = s.MiningSeed;
        j.ComputorPublicKey = s.ComputorPublicKey;
        j.ComputorId = s.ComputorId;
        j.Nonce = s.Nonce;
        j.TickNumber = s.TickNumber;
        j.Timestamp = s.Timestamp;
        if (string.IsNullOrEmpty(j.Algorithm)) j.Algorithm = s.Algorithm;
    }

    public Job Enqueue(string hash, int? epoch = null, string? build = null, bool low = false, Solution? sol = null)
    {
        lock (_lock)
        {
            if (_byHash.TryGetValue(hash, out var existingId))
            {
                // Backfill epoch/build/inputs if a later enqueue learned them.
                var ex = _jobs[existingId];
                if (ex.Epoch is null && epoch is not null) ex.Epoch = epoch;
                if (ex.Build is null && build is not null) ex.Build = build;
                if (ex.MiningSeed is null && sol is not null) StoreInputs(ex, sol);
                return ex;
            }
            var id = $"job_{Base36(Now())}_{++_seq}";
            var job = new Job { Id = id, Hash = hash, Status = "pending", CreatedAt = Now(),
                Epoch = epoch, Build = build, Low = low };
            if (sol is not null) StoreInputs(job, sol);
            _jobs[id] = job;
            _byHash[hash] = id;
            Requeue(job);
            return job;
        }
    }

    // Release a leased job back to pending immediately (e.g. the API couldn't enrich it this time),
    // so it's retried without waiting for the lease to expire.
    public void Release(string jobId)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(jobId, out var job) && job.Status == "leased")
            {
                job.Status = "pending";
                job.LeasedTo = null;
                job.LeaseExpiry = 0;
                RequeueSmart(job);
            }
        }
    }

    // Number of workers seen within the given window — lets the backfill scale its feed to demand.
    public int WorkersActive(long windowMs)
    {
        lock (_lock)
        {
            var now = Now();
            return _workers.Count(kv => now - kv.Value.LastSeen < windowMs);
        }
    }

    // Queue load, used by the scheduler to decide whether there's spare capacity for backfill.
    public (int Pending, int Leased) Load()
    {
        lock (_lock)
        {
            int pending = 0, leased = 0;
            foreach (var j in _jobs.Values)
            {
                if (j.Status == "pending") pending++;
                else if (j.Status == "leased") leased++;
            }
            return (pending, leased);
        }
    }

    private void Reap()
    {
        var now = Now();
        List<Job>? reaped = null;
        foreach (var job in _jobs.Values)
        {
            if (job.Status == "leased" && job.LeaseExpiry < now)
            {
                job.Status = "pending";
                job.LeasedTo = null;
                RequeueSmart(job);   // expired lease on an in-progress job → back to the fast lane
            }
            // Safety net: a job that reached consensus but was never finalized (caller crashed
            // mid-flight) must not linger in "resolving" forever — retire it so it can't pile up.
            else if (job.Status == "resolving" && job.LeaseExpiry != 0 && job.LeaseExpiry < now)
            {
                job.Status = "done";
                (reaped ??= new()).Add(job);
            }
        }
        // Retire outside the enumeration — RetireDone mutates _jobs.
        if (reaped != null) foreach (var j in reaped) RetireDone(j);
    }

    // True when the worker is trusted enough to receive new work.
    private bool WorkerAllowed(string workerId)
        => !_workers.TryGetValue(workerId, out var w) || w.Reputation >= _minReputation;

    public Job? Claim(string workerId, IReadOnlyCollection<string>? supportedBuilds = null)
    {
        lock (_lock)
        {
            Reap();
            var w = _workers.TryGetValue(workerId, out var wi) ? wi : new WorkerInfo();
            w.LastSeen = Now();
            _workers[workerId] = w;

            // Reputation gate: a worker that repeatedly disagrees with the referee/majority
            // is deprioritized and gets no new jobs (no stake => reputation-based exclusion).
            if (!WorkerAllowed(workerId)) return null;

            var skipped = new List<Job>();
            Job? picked = null;
            // Corroboration lane first (finish in-progress consensus), then current epoch, then
            // historical backfill.
            foreach (var q in new[] { _pendingCorroborate, _pending, _pendingLow })
            {
                while (q.Count > 0)
                {
                    var id = q.Dequeue();
                    if (!_jobs.TryGetValue(id, out var job) || job.Status != "pending") continue;
                    // Don't hand a worker a job it already submitted a result for (N-of-M needs
                    // DISTINCT workers to corroborate).
                    if (job.Results.Any(r => r.Worker == workerId)) { skipped.Add(job); continue; }
                    // Capability match: never hand a worker a job whose era-binary it lacks.
                    if (job.Build is not null && supportedBuilds is not null &&
                        !supportedBuilds.Contains(job.Build)) { skipped.Add(job); continue; }
                    job.Status = "leased";
                    job.LeasedTo = workerId;
                    job.LeaseExpiry = Now() + _leaseMs;
                    picked = job;
                    break;
                }
                if (picked is not null) break;
            }
            foreach (var s in skipped) RequeueSmart(s);   // keep in-progress jobs in the fast lane
            return picked;
        }
    }

    public enum ConsensusState { Pending, Confirmed, Conflicted }

    public sealed record SubmitOutcome(
        Job? Job, bool Accepted, ConsensusState State,
        string? WinningGenomeId, long? WinningScore, string? Error = null);

    // Record a worker's result and decide the next step. Does NO I/O — the caller runs the
    // referee (for conflicts) and persists, then calls Finalize. A confirmed/conflicted job is
    // parked in "resolving" so no further worker can claim it while the caller finalizes.
    public SubmitOutcome Submit(string id, string workerId, long? score, bool? passes,
        string? genomeId, string? verifierVersion, JsonNode? reconstruction)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job))
                return new SubmitOutcome(null, false, ConsensusState.Pending, null, null, "unknown_job");
            if (job.Status == "done" || job.Status == "resolving")
                return new SubmitOutcome(job, false, ConsensusState.Pending, null, null, "already_resolved");
            if (job.Results.Any(r => r.Worker == workerId))
                return new SubmitOutcome(job, false, ConsensusState.Pending, null, null, "duplicate_worker");

            bool pinning = _expectedVersions.Count > 0;
            // A null version is assumed trusted; otherwise it must be one of the trusted builds
            // (current OR historical). This is what lets qubic-atlas-hist-1 results count.
            bool versionOk = !pinning || verifierVersion is null || _expectedVersions.Contains(verifierVersion);
            bool eligible = !string.IsNullOrEmpty(genomeId) && versionOk;
            if (pinning && !string.IsNullOrEmpty(verifierVersion) && !_expectedVersions.Contains(verifierVersion))
                Console.WriteLine($"[consensus] job {id}: worker {workerId} on unexpected verifier_version " +
                                  $"'{verifierVersion}' (expected one of '{ExpectedVersion}') — recorded, not counted");

            job.Results.Add(new JobResult
            {
                Worker = workerId, Score = score, Passes = passes, GenomeId = genomeId,
                VerifierVersion = verifierVersion, Eligible = eligible, At = Now(),
            });
            if (reconstruction is not null) job.Reconstruction = reconstruction;

            var w = _workers.TryGetValue(workerId, out var wi) ? wi : new WorkerInfo();
            w.Completed += 1;
            w.LastSeen = Now();
            _workers[workerId] = w;

            // Group ELIGIBLE results by genome_id.
            var tally = new Dictionary<string, int>();
            foreach (var r in job.Results.Where(r => r.Eligible))
                tally[r.GenomeId!] = tally.GetValueOrDefault(r.GenomeId!) + 1;

            string? bestGenome = null; int bestCount = 0;
            foreach (var kv in tally)
                if (kv.Value > bestCount) { bestCount = kv.Value; bestGenome = kv.Key; }
            job.Confirmations = bestCount;

            // Conflict: two or more distinct genome_ids among honest-version submissions.
            if (tally.Count >= 2)
            {
                job.Status = "resolving";
                job.LeasedTo = null;
                job.LeaseExpiry = Now() + 120000;   // finalize deadline (reaped if the caller never finalizes)
                return new SubmitOutcome(job, true, ConsensusState.Conflicted, bestGenome, ScoreOf(job, bestGenome));
            }

            // Confirmed: enough workers agree on the one genome.
            if (bestCount >= RequiredConfirmations && bestGenome is not null)
            {
                job.Status = "resolving";
                job.LeasedTo = null;
                job.LeaseExpiry = Now() + 120000;   // finalize deadline (reaped if the caller never finalizes)
                return new SubmitOutcome(job, true, ConsensusState.Confirmed, bestGenome, ScoreOf(job, bestGenome));
            }

            // Not enough corroboration yet — re-open in the FAST lane so another distinct worker
            // finishes it immediately, instead of it sinking behind freshly-enqueued proofs.
            job.Status = "pending";
            job.LeasedTo = null;
            RequeueSmart(job);
            return new SubmitOutcome(job, true, ConsensusState.Pending, bestGenome, ScoreOf(job, bestGenome));
        }
    }

    private static long? ScoreOf(Job job, string? genome)
        => genome is null ? null : job.Results.FirstOrDefault(r => r.Eligible && r.GenomeId == genome)?.Score;

    // Apply the authoritative verdict, update worker reputations, and close the job.
    // For a clean confirmation the authoritative genome is the agreed one (referee not run);
    // for a conflict it is the referee's genome. Workers matching it get +1 agreed, eligible
    // dissenters +1 disagreed.
    public Job? Finalize(string id, string authoritativeGenome, long? score, bool? passes,
        long? threshold, string? algorithm, bool resolvedByReferee)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(id, out var job)) return null;

            int matching = 0; bool anyDissent = false;
            foreach (var r in job.Results)
            {
                if (!r.Eligible) continue; // unexpected-version results neither help nor hurt
                var w = _workers.TryGetValue(r.Worker, out var wi) ? wi : new WorkerInfo();
                if (r.GenomeId == authoritativeGenome) { w.Agreed += 1; matching++; }
                else { w.Disagreed += 1; anyDissent = true; }
                _workers[r.Worker] = w;
            }

            job.Status = "done";
            job.VerifiedScore = score;
            job.GenomeId = authoritativeGenome;
            job.Passes = passes;
            job.Threshold = threshold;
            job.Algorithm = algorithm;
            job.Confirmations = matching;
            job.Agreed = !anyDissent;
            job.ResolvedByReferee = resolvedByReferee;
            _byHash.Remove(job.Hash);
            RetireDone(job);
            return job;
        }
    }

    public Job? Get(string id)
    {
        lock (_lock) { return _jobs.TryGetValue(id, out var j) ? j : null; }
    }

    public object Stats()
    {
        lock (_lock)
        {
            int pending = 0, leased = 0, done = 0, resolving = 0;
            foreach (var j in _jobs.Values)
            {
                switch (j.Status)
                {
                    case "pending": pending++; break;
                    case "leased": leased++; break;
                    case "resolving": resolving++; break;
                    case "done": done++; break;
                }
            }
            var now = Now();
            const long activeWindowMs = 120_000;    // 2 min: a worker idle longer drops off the list
            const long forgetMs = 3_600_000;        // 1 h: gone this long → forgotten entirely (frees memory)
            // Memory hygiene: ephemeral worker identities (workers without a persisted key regenerate
            // on restart) accumulate forever otherwise. Drop the long-gone ones.
            foreach (var k in _workers.Where(kv => now - kv.Value.LastSeen > forgetMs).Select(kv => kv.Key).ToList())
                _workers.Remove(k);
            var workers = _workers
                .Where(kv => now - kv.Value.LastSeen < activeWindowMs)   // only recently-active workers
                .OrderByDescending(kv => kv.Value.LastSeen)
                .Select(kv => new
                {
                    id = kv.Key,
                    completed = kv.Value.Completed,
                    agreed = kv.Value.Agreed,
                    disagreed = kv.Value.Disagreed,
                    reputation = kv.Value.Reputation,
                    trusted = kv.Value.Reputation >= _minReputation,
                    online = now - kv.Value.LastSeen < 30000,   // "live" (green) vs merely recently-active
                }).ToList();
            return new
            {
                pending,
                leased,
                resolving,
                done,
                total = _jobs.Count,
                requiredConfirmations = RequiredConfirmations,
                expectedVerifierVersion = ExpectedVersion,
                minReputation = _minReputation,
                workers,
                workersOnline = workers.Count,
            };
        }
    }

    public object Recent(int n = 20)
    {
        lock (_lock)
        {
            return _jobs.Values
                .OrderByDescending(j => j.CreatedAt)
                .Take(n)
                .Select(j => new
                {
                    id = j.Id,
                    hash = j.Hash,
                    status = j.Status,
                    confirmations = j.Confirmations,
                    verifiedScore = j.VerifiedScore,
                    agreed = j.Agreed,
                    resolvedByReferee = j.ResolvedByReferee,
                    genomeId = j.GenomeId,
                    results = j.Results.Count,
                })
                .ToList();
        }
    }

    public HashSet<string> ActiveHashes()
    {
        lock (_lock) { return new HashSet<string>(_byHash.Keys); }
    }
}
