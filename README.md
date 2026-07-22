# Qubic Atlas

A decentralized **verification explorer** for Qubic ANN mining Proofs. Unlike a normal explorer,
it does not ask you to trust a scoring API — a network of independent workers **re-runs the
canonical Qubic Core scorer** on the public inputs of each Proof and reconstructs the neural
genome (graph + metrics + mutation trace), then agrees on the result by consensus.

> Validated: Proof `jfxwtx…dblcel` re-scores to **321** (threshold 316) with 1024 neurons,
> 372,741 synapses and 3 accepted mutations — matching the on-chain result **field-for-field**.
> Historical epochs reproduce byte-exact too (e.g. epoch 214 → 76939 via the era-correct build).

## Why it's decentralized

Every input needed to score a Proof is public: the per-epoch `miningSeed` (expands via
KangarooTwelve into the shared 512 MB random pool), the `computorPublicKey`, the `nonce`
(its parity selects HyperIdentity vs Addition), and the epoch's parameter set (pinned to a
Core release). The verifier is the **actual Core C++ scorer, compiled unmodified**, so its
output is bit-identical to what computors run. Genomes are content-addressed
(`annGenomeId = sha256`), so independent verifiers anywhere produce the **same hash** and
cross-check each other with zero trust. Not one oracle — a reproducible computation anyone
can run: `docker run -d qubiclab/atlas-worker:latest`.

## Architecture

```
frontend/   Vue 3 SPA behind nginx — Proofs browser (reusable ProofsTable), epochs,
            computors, Proof detail (neural-graph viz + mutation replay), leaderboard.
            Central CSS class system, no inline styles.
backend/    ASP.NET Core service — RPC ingestion (firehose + by-tick), on-demand verify,
            the distributed JobQueue (claim → signed result → N-of-M consensus → referee),
            ClickHouse persistence of OUR verdicts. Reuses Qubic.Crypto / Qubic.Rpc.
  AtlasWorker/  the community worker: claims jobs, runs the era-correct verifier binary,
                signs the result with its Qubic identity, submits.
scorer/     The decentralized verifier: Qubic Core scorer compiled on Linux + instrumented
            to emit reconstruction JSON. Bundles one binary per historical Core era
            (verifier, verifier-build0/1/2) for multi-epoch backward compatibility.
```

Data source: **public Qubic Archive only** (`QUBIC_RPC`, default `https://rpc.qubic.org`). The
backend is fully standalone — it self-populates the recent-Proof index from the RPC firehose and
persists its own verdicts to ClickHouse; there is no separate indexer process. ANN Proofs are
on-chain transactions:

- destination `AAA…FXIB`, `inputType 2`, `inputSize 64`
- `input = miningSeed(32) ‖ nonce(32)`; `source` identity → 32-byte computor pubkey (base-26 decode)
- read via the identity firehose `/v2/identities/{dest}/transactions?desc=true` (recent, paginated)
  or by tick `/v1/ticks/{tick}/transactions` (historical backfill, tick-desc)

So the verifier's inputs come straight from the chain; nobody is trusted.

## Multi-epoch (backward compatibility)

The `dual_hyperidentity_addition` scorer was introduced at Core v1.275 (epoch 197). Its Addition
path changed parameters across four builds since. Atlas bundles one binary per era and a registry
(`backend/Registry.cs`) mapping each epoch → build + thresholds, so any Proof from epoch 197
onward verifies with the byte-exact scorer that produced it. Workers advertise which builds they
carry on claim; the server only hands a worker jobs it can run. See `MULTI_EPOCH.md`.

The scheduler prioritizes the **latest** epoch (high-priority lane) and fills spare worker
capacity with **historical backfill** (low-priority lane, tick-descending), so live Proofs are
never starved. Epochs ≤196 use a different algorithm and are out of scope for now.

## The scorer build (`scorer/coretree`)

The Core scorer targets MSVC/UEFI. To build it on Linux/clang, `coretree/` is a copy of
`qubic/src` + `qubic/lib` with three **cosmetic** headers replaced by no-op shims
(`console_logging.h`, `concurrency.h`, `profiling.h` — logging/locks/profiling, none of
which affect scoring), the wide-string filename arrays in `public_settings.h` retyped to
16-bit `wchar_t`, and a small instrumentation hook to record the mutation trace. Build flags:
`clang++ -mavx2 -mbmi -mbmi2 -fshort-wchar -DNO_UEFI` (AVX2 only — runs on any x86-64 since ~2013,
no AVX-512 required). The historical era binaries are built by `scorer/build_hist.sh <tag> <buildN>`.

## Run

Everything is Dockerized:

```bash
docker compose up -d        # api + frontend + clickhouse + workers
# open http://localhost:8080
```

See `WORKER.md` for running a community worker, `CLICKHOUSE.md` for the storage model, `AUTH.md`
for signed submissions, and `deploy/` for the production compose (Cloudflare tunnel, no host ports).

## Scope

- `dual_hyperidentity_addition` family, epochs 197–current (Core v1.275 → v1.299) and forward.
- Full graph reconstruction for **both** HyperIdentity and Addition.
- Permissionless + signed submissions (Qubic SchnorrQ identities), optional allowlist,
  N-of-M consensus with referee conflict resolution and reputation exclusion.

## Roadmap

See **[ROADMAP.md](ROADMAP.md)**. Highlights:

- **Future mining algorithms, automatically** — when Qubic adopts a new proof-of-work (e.g. a
  future BPP/"BPP9000" or ant-colony variant), we ship an updated verifier in `:latest`. Community
  workers running `qubiclab/atlas-worker:latest` pull it and verify the new proofs with **no
  reconfiguration** — the coordinator advertises which build each proof needs.
- **Free public results API** — documented, open REST endpoints so anyone can build on Atlas's
  verified results.
- **In-browser (WebAssembly) verification**, **multi-arch (ARM) images**, **contributor rewards**,
  older-epoch coverage, and more.

Everything is **open source**: <https://github.com/qubic-atlas/core>
