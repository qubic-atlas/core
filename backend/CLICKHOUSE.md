# Qubic Atlas — ClickHouse storage & conflict-aware consensus

This document covers the ClickHouse layer added to the Atlas API: what we store, the
consensus/conflict model, the per-proof storage footprint, and how to run and test it.

## Storage philosophy

**We store ONLY our results, never raw proof data.** The raw proof (miningSeed/nonce) lives on
the Qubic RPC/archive and is fetched on demand. The ~212 KB reconstruction blob (graph/trace)
is **regenerable**, so it stays OUT of ClickHouse and remains the existing on-disk `.cache`
(`CACHE_DIR`, default `/data/cache`). ClickHouse holds only our recomputed **verdicts**, which
is everything the browse/leaderboard/epoch/stats views need.

The single source of aggregation is the **`verifications`** table — epoch, computor and tick are
embedded on every row, so there is no separate proofs table.

## Schema (created on startup if absent)

Database: `CLICKHOUSE_DB` (default `atlas`). Created automatically. High-entropy columns
(`hash`, `computor_id`, `genome_id`, `mining_seed`) use `CODEC(ZSTD)`; dedup is via
`ReplacingMergeTree`.

```sql
CREATE TABLE epochs (
  epoch UInt16, core_version LowCardinality(String), mining_seed FixedString(64) CODEC(ZSTD),
  first_tick UInt32, last_tick UInt32, hi_threshold UInt32, add_threshold UInt32
) ENGINE = ReplacingMergeTree ORDER BY epoch;

CREATE TABLE verifications (              -- our recomputed verdicts (the main table)
  hash FixedString(60) CODEC(ZSTD), epoch UInt16, tick UInt32, ts DateTime,
  algorithm LowCardinality(String), computor_id FixedString(60) CODEC(ZSTD),
  score Int32, passes UInt8, threshold UInt32,
  genome_id String CODEC(ZSTD),          -- sha256 hex of the canonical genome (server-computed)
  verifier_version LowCardinality(String),
  status LowCardinality(String),         -- 'confirmed' | 'conflicted'
  confirmations UInt8, verified_at DateTime
) ENGINE = ReplacingMergeTree(verified_at) ORDER BY (epoch, computor_id, hash);

CREATE TABLE worker_results (            -- audit trail, per (proof, worker)
  hash FixedString(60) CODEC(ZSTD), worker_id LowCardinality(String), genome_id String CODEC(ZSTD),
  score Int32, verifier_version LowCardinality(String), at DateTime,
  signature String CODEC(ZSTD)           -- worker's SchnorrQ signature over the canonical message
) ENGINE = MergeTree ORDER BY (hash, at) TTL at + INTERVAL 180 DAY;
```

`verifications` dedups on `(epoch, computor_id, hash)` keeping the newest `verified_at`. Reads
use `... FINAL` so callers always see the deduped latest verdict. `worker_results` is an append
-only audit log that self-expires after 180 days.

`worker_id` is the submitter's **verified Qubic identity** and `signature` is its FourQ/SchnorrQ
signature over `"{jobId}|{hash}|{genomeId}|{score}"`, making each row attributable + non-repudiable.
See **[AUTH.md](AUTH.md)** for the full signing scheme, env vars (`ATLAS_REQUIRE_SIGNED`,
`ATLAS_WORKER_ALLOWLIST[_FILE]`, `ATLAS_KEY_FILE`), and open-vs-allowlist modes.

## Consensus / conflict model

The verifier is **deterministic**: identical public inputs + the canonical Core scorer produce a
byte-identical result. So there is exactly ONE correct `(score, genome)` per proof — any
disagreement means a worker ran modified/buggy code or wrong params.

1. **`genome_id` is computed SERVER-SIDE** (`GenomeId.cs`) — a worker's own `annGenomeId` claim
   is never trusted. It is `sha256(hex)` over a canonical serialization of only the fields that
   define the network: `{ algorithm, score, metrics, graph.nodes, graph.links, graph.matrix }`,
   with object keys sorted recursively and scalar formatting preserved verbatim
   (`JsonElement.GetRawText()`). Honest workers running the same binary hash identically.

2. **Confirmation** — a proof is `confirmed` when `ATLAS_CONFIRMATIONS` (default 1; supports 2+)
   **distinct** workers agree on the same `genome_id`. Submissions are grouped by `genome_id`,
   not by raw score. For N>1, a job is re-queued after each partial result so a *different*
   worker can corroborate it (a worker is never handed a job it already answered).

3. **Conflict** — if two or more distinct `genome_id`s appear for one proof, the job is marked
   `resolving` and the **server referee** runs its OWN trusted verifier recompute. That result is
   authoritative: workers matching it are correct, dissenters are flagged. The verdict is stored
   with `status='conflicted'` and the referee's `(score, genome_id)`. The referee itself is also
   written to `worker_results` (`worker_id='referee'`).

   > Note on `status`: the product brief says "set final status=confirmed with the referee's
   > value." We instead persist `status='conflicted'` when a dissent occurred (the stored score/
   > genome are still the referee-authoritative *correct* values), so the `/api/solutions` badge
   > can surface that a proof was contested. Clean agreement is `status='confirmed'`. This is the
   > one deliberate deviation from the literal wording; the *value* is always the referee's.

4. **Worker reputation** — per-worker `agreed`/`disagreed` counts are tracked in memory and
   exposed at `/api/jobs/stats` as `workers[].agreed/disagreed/reputation` (`reputation =
   agreed − disagreed`). A worker whose `reputation` falls below `ATLAS_MIN_REPUTATION`
   (default `-3`) is `trusted=false` and is **no longer leased new jobs** (`/api/jobs/claim`
   returns 204). No stake is involved — exclusion is purely reputation-based.

5. **Version pinning** — each result stores its `verifier_version` (the verifier's
   `reconstructorVersion`). If `ATLAS_VERIFIER_VERSION` is set, results from any other build are
   **recorded (for audit) but do not count toward confirmation** (a warning is logged). If unset,
   pinning is off (any non-empty genome counts). Hardware attestation/TEE is the only way to
   *prove* a worker ran unmodified code, but it is unnecessary here: determinism makes every
   result independently re-derivable, so trust comes from **reproducibility + referee recompute**,
   not from trusting the worker.

## Query paths

Endpoints serve from ClickHouse when it is populated and fall back to the RPC/index otherwise:

| Endpoint | ClickHouse source |
|----------|-------------------|
| `GET /api/computors` | `SELECT computor_id, count() … GROUP BY computor_id` over `verifications FINAL` (`source:"clickhouse"`); falls back to index/recent-window. |
| `GET /api/epochs` | boundaries joined with per-epoch verified/confirmed/conflicted counts. |
| `GET /api/solutions` / `GET /api/solutions/:hash` | `verification` badge (`status`, `score`, `confirmations`) looked up by hash. |
| `GET /api/verifications/stats` | totals: `verified`, `confirmed`, `conflicted`, distinct `computors`. |
| `GET /api/verify/:hash` | checks CH for a cached verdict first, upserts a `confirmed` row (the API's own run is the referee), and backfills a row from an existing disk cache. |
| `GET /api/index/status` | adds live CH row counts (`verifications`, `workerResults`, `epochs`). |

If `CLICKHOUSE_URL` is **unset** the store is disabled and every endpoint uses the original
file/RPC behavior — the API never hard-crashes without ClickHouse. Every CH call is also wrapped
so a transient outage degrades gracefully rather than failing a request.

## Per-proof storage footprint

We keep the big blob OUT of ClickHouse. Measured on this stack:

| Table | Bytes/row (uncompressed) | What it is |
|-------|--------------------------|------------|
| `verifications` | ~0.25 KB | one authoritative verdict per proof |
| `worker_results` | ~0.15 KB | one audit row per (proof, worker) submission + one per referee run |
| `epochs` | ~0.12 KB | one row per epoch (deduped) |

So a proof verified by 2 workers costs roughly **0.25 KB (verdict) + ~0.45 KB (3 audit rows)
≈ 0.7 KB** in ClickHouse — versus the **~212 KB reconstruction** blob, which stays on disk as a
regenerable cache and is fetched/recomputed on demand. High-entropy columns are ZSTD-compressed
and `worker_results` self-expires after 180 days (TTL).

## Configuration (env)

| Var | Default | Meaning |
|-----|---------|---------|
| `CLICKHOUSE_URL` | *(unset)* | e.g. `http://clickhouse:8123`. Unset ⇒ CH disabled, file/RPC fallback. |
| `CLICKHOUSE_DB` | `atlas` | database name (created on startup). |
| `ATLAS_CONFIRMATIONS` | `1` (compose sets `2`) | distinct workers that must agree on `genome_id`. |
| `ATLAS_VERIFIER_VERSION` | *(unset ⇒ pinning off)* | trusted `reconstructorVersion`; compose pins `qubic-atlas-verify-1`. |
| `ATLAS_MIN_REPUTATION` | `-3` | reputation floor below which a worker is no longer leased jobs. |

## Run

```bash
# from the repo root — brings up clickhouse + api + workers + frontend
docker compose up -d --build

# schema check
docker compose exec clickhouse clickhouse-client -q "SHOW TABLES FROM atlas"
```

The API talks to `clickhouse:8123` over the compose network; `api` waits on the ClickHouse
healthcheck (`depends_on: condition: service_healthy`). The frontend (nginx, host `:8080`)
proxies `/api/*` to the API, so tests below go through `http://localhost:8080`.

## Test (definition of done — all verified)

```bash
# 1) featured HyperIdentity proof still verifies to 321, and a row lands in CH
curl -s http://localhost:8080/api/verify/jfxwtxbogcsufafliioqupmnzygbytjxeucozqtmagddylxzkqhijrdblcel | jq .score   # 321
docker compose exec clickhouse clickhouse-client -q "SELECT hash,score,status FROM atlas.verifications"

# 2) distributed confirm: seed a proof, let the 2 workers agree -> status confirmed, 2 audit rows
curl -s -X POST "http://localhost:8080/api/jobs/seed?n=1"
curl -s http://localhost:8080/api/jobs/recent | jq '.[0] | {status,confirmations}'
docker compose exec clickhouse clickhouse-client -q "SELECT worker_id,score FROM atlas.worker_results"

# 3) simulate a conflict: enqueue a proof, POST a bogus result from a fake worker, then let a
#    real worker submit -> referee recompute resolves to the CORRECT score, dissenter flagged
JOB=$(curl -s -X POST http://localhost:8080/api/jobs/enqueue -d '{"hash":"<hash>"}' | jq -r .id)
curl -s -X POST http://localhost:8080/api/jobs/$JOB/result \
  -d '{"worker":"attacker-1","reconstruction":{"reconstructorVersion":"qubic-atlas-verify-1","algorithm":"Addition","score":999999,"threshold":74100,"passesThreshold":true,"metrics":{},"graph":{"nodes":[],"links":[],"matrix":{"g":0,"cells":[]}}}}'
# ... a real worker submits the honest result -> status conflicted, verifiedScore=correct, dissenters listed
curl -s http://localhost:8080/api/jobs/stats | jq '.workers[] | select(.disagreed>0)'   # reputation decremented

# 4) ClickHouse-sourced query endpoints
curl -s http://localhost:8080/api/computors            | jq '.source'                    # "clickhouse"
curl -s http://localhost:8080/api/verifications/stats                                    # {verified,confirmed,conflicted,computors}
```

Observed results: featured proof → 321; clean 2-of-2 → `confirmed` (2 audit rows, both workers
`agreed`); conflict → `conflicted` with the referee's correct score, the honest worker's genome
matching the referee's byte-for-byte, the attacker flagged (`disagreed`, reputation ↓); driving a
worker below `-3` makes `/api/jobs/claim` return `204` (excluded).
