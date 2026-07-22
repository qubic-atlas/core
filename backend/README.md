# Qubic Atlas — backend (ASP.NET Core, .NET 10)

A 1:1 C# port of the Node/Express reference in `../server/`. Serves live Qubic data and
ANN mining **Proofs** (on-chain "solutions") from the **public Qubic archive only** (no
third-party relay), reconstructs + verifies them locally with the native C++ verifier, and
runs a distributed verifier job queue that independent workers cross-check.

- **API project**: `QubicAtlas.csproj` (this directory) — the web API.
- **Worker project**: `AtlasWorker/` — a console client that claims jobs and runs the verifier.
- Identity → public key uses the **Qubic.Crypto** NuGet package (`QubicCrypt.GetPublicKeyFromIdentity`),
  vendored in `./nupkgs` and wired via `nuget.config`. A base26 fallback is built in.

## Endpoints (paths identical to the Node reference)

| Method | Path | Notes |
|--------|------|-------|
| GET | `/api/live/tick-info` | proxy `${RPC}/v1/tick-info` |
| GET | `/api/solutions?limit&offset&algorithm&epoch` | index or firehose source |
| GET | `/api/solutions/:hash` | single Proof |
| GET | `/api/ticks/:tick/solutions` | Proofs in a tick |
| GET | `/api/epochs` | derived from `/v1/status` + registry |
| GET | `/api/computors` | leaderboard from the index |
| GET | `/api/verify/:hash` | fetch inputs, run verifier, cache by hash (`?nocache` bypasses) |
| GET | `/api/index/status` | index stats |
| GET | `/api/health` | `{ ok, rpc, solutionDest, verifier, relay }` |
| POST | `/api/jobs/enqueue` `{hash}` | enqueue a Proof |
| POST | `/api/jobs/seed?n=5` | enqueue N recent Proofs from the firehose |
| GET | `/api/jobs/claim?worker=ID` | lease a job (or 204) |
| POST | `/api/jobs/:id/result` `{worker, reconstruction}` | submit; on `done` writes the verify cache |
| GET | `/api/jobs/stats` / `/api/jobs/recent` | queue introspection (incl. per-worker `agreed`/`disagreed`/`reputation`) |
| GET | `/api/verifications/stats` | ClickHouse totals: `verified`, `confirmed`, `conflicted`, distinct `computors` |

**ClickHouse storage & conflict-aware consensus** are documented in [CLICKHOUSE.md](CLICKHOUSE.md):
we persist only our recomputed verdicts (`verifications`), an audit trail (`worker_results`) and
`epochs`; the ~212 KB reconstruction blob stays on the disk `.cache`. Consensus groups worker
submissions by a **server-computed `genome_id`**; disagreements are resolved by a trusted
**referee recompute**; dissenting workers lose reputation and are eventually not leased jobs.
`/api/computors`, `/api/epochs`, `/api/solutions`, `/api/verifications/stats` and
`/api/index/status` serve from ClickHouse when populated and fall back to RPC/index otherwise.

If `WEB_DIST` points at a built SPA, it is served with `index.html` un-cached and hashed
`assets/*` marked immutable (avoids the stale-bundle white-screen).

## Configuration (env)

| Var | Default | Meaning |
|-----|---------|---------|
| `PORT` | `8099` | listen port |
| `QUBIC_RPC` | `https://rpc.qubic.org` | archive/RPC base |
| `SOLUTION_DEST` | `AAAA…FXIB` | ANN solution destination address |
| `VERIFIER` | `/usr/local/bin/verifier` (else `../scorer/verifier`) | verifier binary |
| `CACHE_DIR` | `./.cache` | verify reconstruction cache |
| `ATLAS_INDEX_CAP` | `1000` | recent Proofs pulled from the firehose into the in-memory index |
| `WEB_DIST` | `../frontend/dist` | optional SPA to serve |
| `ATLAS_CONFIRMATIONS` | `1` | distinct workers that must agree on the `genome_id` to confirm |
| `CLICKHOUSE_URL` | *(unset)* | e.g. `http://clickhouse:8123`; unset ⇒ CH disabled, file/RPC fallback |
| `CLICKHOUSE_DB` | `atlas` | ClickHouse database (created on startup) |
| `ATLAS_VERIFIER_VERSION` | *(unset ⇒ pinning off)* | trusted `reconstructorVersion`; other builds are recorded but don't count |
| `ATLAS_MIN_REPUTATION` | `-3` | reputation floor below which a worker is no longer leased jobs |

Worker env: `ATLAS_URL` (default `http://localhost:8099`), `WORKER_ID`, `VERIFIER`,
`POLL_INTERVAL_MS` (default `3000`). Pass `--once` (or `RUN_ONCE=1`) to process one job and exit.

## Run locally

```bash
# API
PORT=8099 \
VERIFIER=../scorer/verifier \
dotnet run -c Release

# Worker (in another shell)
cd AtlasWorker
ATLAS_URL=http://localhost:8095 WORKER_ID=w1 VERIFIER=../../scorer/verifier dotnet run -c Release
```

Smoke test:
```bash
curl -s localhost:8095/api/health
curl -s localhost:8095/api/live/tick-info
curl -s "localhost:8095/api/solutions?limit=3"
curl -s localhost:8095/api/verify/jfxwtxbogcsufafliioqupmnzygbytjxeucozqtmagddylxzkqhijrdblcel   # score 321, passesThreshold true
# distributed flow
curl -s -X POST localhost:8095/api/jobs/enqueue -H 'content-type: application/json' -d '{"hash":"<hash>"}'
(cd AtlasWorker && ATLAS_URL=http://localhost:8095 WORKER_ID=w1 VERIFIER=../../scorer/verifier dotnet run -c Release -- --once)
curl -s localhost:8095/api/jobs/stats
```

## Docker

Both images copy the native verifier binary from the prebuilt `qubic-atlas/verifier:latest`
image (`COPY --from=…`), so no separate build of the C++ scorer is needed.

```bash
# from this directory (build context = backend/, so nuget.config + nupkgs are available)
docker build -t qubic-atlas/api:latest -f Dockerfile .
docker build -t qubic-atlas/worker:latest -f AtlasWorker/Dockerfile .

# run the API (exposes 8099); mount an index dir if you have one
docker run -d -p 8099:8099 \
  -v "$PWD/.index:/data/index" \
  qubic-atlas/api:latest

# run a worker against it
docker run --rm --network host \
  -e ATLAS_URL=http://localhost:8099 -e WORKER_ID=w1 \
  qubic-atlas/worker:latest
```

## Verified against the Node reference

Run side-by-side (`PORT=8099 node ../server/index.js`) and diffed:
- `/api/solutions/:hash`, `/api/solutions` (firehose **and** index paths), `/api/epochs`,
  `/api/computors`, `/api/index/status`, `/api/verify/:hash` — **byte-identical** JSON
  (same keys, order, and values; verifier matrix cells match exactly).

## Deviations from the Node reference

- **`WEB_DIST` default** is `../frontend/dist` (per the port brief) rather than the Node
  server's `../web/dist`. Override with the env var if needed.
- **Job API routes** (`/api/jobs/*`) are implemented per `CONTRACT.md`. The reference
  `index.js` constructs the `JobQueue` but never wired HTTP routes to it, so the exact
  request/response envelopes here follow the contract, while the queue **logic**
  (lease/reap, modal-score consensus, confirmations) is a faithful port of `jobs.js`.
- On job `done`, the reconstruction submitted by the worker is written to the verify cache
  tagged `source: "distributed-network"` (vs `"public-archive"` for a direct `/api/verify`).
- `timestamp` is omitted from a Proof when the source transaction lacks it (mirrors JS
  dropping `undefined`), rather than emitted as `null`.
- The tick-walking indexer itself is not re-implemented; the API reads the existing
  `solutions.jsonl` produced by the Node `indexer.js` (as the brief allows).
