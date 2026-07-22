# Multi-epoch (backward-compatible) verification

Qubic Atlas reproduces ANN mining scores **byte-exact** by shelling out to the real Qubic Core
scorer compiled to a native CLI. The scoring code changed across Core releases, so the verifier
image now ships **four era-specific binaries**, each reproducing its epochs exactly:

| binary | env / path | Core span | epochs |
|--------|-----------|-----------|--------|
| **build3** (current) | `VERIFIER` (default `/usr/local/bin/verifier`) | v1.293–v1.299 | **215–222** |
| build0 | `<dir>/verifier-build0` | v1.275–v1.281 | 197–203 |
| build1 | `<dir>/verifier-build1` | v1.282–v1.284 | 204–206 |
| build2 | `<dir>/verifier-build2` | v1.285–v1.292 | 207–214 |

**Epochs ≤196 are UNSUPPORTED** (a different mining algorithm) — they are never enqueued or verified.

CLI is identical for every binary: `verifier[-buildN] <seedHex> <pkHex> <nonceHex> <expected|-1>` →
JSON on stdout. build3 emits the full reconstruction (graph/metrics/trace). The historical binaries
emit a smaller score-only JSON:
`{reconstructorVersion:"qubic-atlas-hist-1", algorithm, score, threshold, passesThreshold, rawScoreValid, historical:true}`.
The score is the verified output; the missing graph/trace is expected and fine.

## 1. Registry (`Registry.cs`)

`EpochBuilds` is the source of truth: `epoch → (build, HyperIdentity threshold, Addition threshold)`
for epochs 197–222. Helpers:

- `bool IsSupported(int epoch)` / `IsSupported(int? epoch)` — is the epoch reproducible by a bundled binary?
- `string? BuildForEpoch(int epoch)` — `"build0".."build3"` or `null`.
- `(int Hi, int Add) ThresholdsForEpoch(int epoch)` and `int ThresholdForEpoch(int epoch, string algorithm)`.
- `string VerifierPathForEpoch(int epoch, string build3Path)` — build3 ⇒ `build3Path` (the `VERIFIER`
  env); build0/1/2 ⇒ sibling `verifier-<build>` in the same directory.
- `const string HistoricalVerifierVersion = "qubic-atlas-hist-1"`, `const int MinSupportedEpoch = 197`.

**Threshold override:** a historical binary carries one build-wide threshold that is imprecise across
the version span it covers, so the server **always overrides** `threshold`/`passesThreshold` with the
exact per-epoch value from the registry. The pass rule is `score >= threshold` for **both** algorithms
(verified against the C++: `score>=threshold` in `verifier.cpp` and `verifier_hist.cpp`).

## 2. Epoch-dispatched verification (`Program.cs`)

Every place that runs a binary now picks it by the proof's epoch (resolved from the tick via
`EpochMap.EpochForTickAsync`):

- **`GET /api/verify/{hash}`** and the **referee recompute** in `POST /api/jobs/{id}/result` use the
  helper `VerifyForEpoch(epoch, seed, pk, nonce)` → runs `Registry.VerifierPathForEpoch(...)` and
  overrides threshold/passes. Both **guard on `IsSupported(epoch)` first**; an unsupported epoch returns
  `422 { error:"epoch_unsupported", epoch }` and **no binary is run**.
- **`PersistVerification`** recomputes `threshold = ThresholdForEpoch(ep, algo)` and `passes = score >= threshold`
  before writing the ClickHouse `verifications` row, so stored verdicts always carry the exact per-epoch
  threshold regardless of what the binary emitted.
- Display endpoints (`/api/solutions`, `/api/solutions/{hash}`, `computors/*`) use a small `ThresholdFor(epoch, algo)`
  helper so the badge threshold matches the proof's epoch.

## 3. Capability-matched claim

- The **worker** advertises which era-binaries it has. It probes `<dir>/verifier-*` and `VERIFIER` at
  startup, or takes the explicit list from `ATLAS_BUILDS` (e.g. `build0,build1,build2,build3`), and sends
  them on `GET /api/jobs/claim?worker=ID&builds=build0,build1,build2,build3`.
- Each `Job` carries its `Epoch` + `Build` (resolved at enqueue time). `Claim(workerId, supportedBuilds)`
  skips any job whose `Build` the worker does not support and leases the next one instead — a worker is
  **never** handed a task it cannot run. (A job with `Build == null`, i.e. epoch not yet resolved, is not
  capability-restricted.)
- The claim response includes `epoch`, `build`, and the per-epoch `threshold`; the worker dispatches to
  the matching binary. If it somehow lacks the binary it **skips without submitting** (the lease expires
  and the job returns to the queue).

**Consensus + version pinning.** `ATLAS_VERIFIER_VERSION` pins the trusted build. Multi-epoch trusts a
**set** of versions: the API passes `"{ATLAS_VERIFIER_VERSION},qubic-atlas-hist-1"` to `JobQueue`, so
verified **historical** results (which report `qubic-atlas-hist-1`) count toward confirmation. Any other
`reconstructorVersion` is still recorded for audit but does not count.

## 4. Scheduler: latest-first + historical backfill (tick desc)

The background auto-enqueue loop (`Program.cs`, near the end) now has two phases per cycle:

1. **PRIORITY — current epoch (always first):** enqueue the newest unverified proofs from the firehose
   (`ListSolutions`), exactly as before. Unsupported epochs are skipped; each job is tagged with its
   epoch/build.
2. **SPARE-CAPACITY BACKFILL:** only when `pending + leased < ATLAS_AUTO_ENQUEUE_BATCH` (i.e. workers have
   caught up on the latest) does it also enqueue a batch of **historical** proofs. A descending in-memory
   cursor walks ticks down from just below the current epoch's initial tick toward epoch 197's first tick,
   calling `SolutionsInTick(tick)` per tick, skipping proofs already confirmed (`VerdictsForAsync`) and any
   whose epoch is unsupported or cannot be strictly classified to a known boundary. Idle workers thus chew
   through history newest-first **without ever starving the current epoch**.

Env knobs: `ATLAS_BACKFILL` (default `true`), `ATLAS_BACKFILL_BATCH` (default `8`), and the optional
`ATLAS_BACKFILL_START_TICK` (start the descending cursor at a specific tick to target a historical range;
default = just below the current epoch). Progress is logged: `[backfill] +N historical proofs (tick~…, epoch …)`.

## `docker-compose.yml`

`ATLAS_BUILDS=build0,build1,build2,build3` is set on the `worker` (the image bundles all four). The
`backend/Dockerfile` (api) and `backend/AtlasWorker/Dockerfile` (worker) now COPY all four binaries
(`verifier` + `verifier-build0/1/2`) from the verifier image; previously only build3 was copied.

## Verification performed

- **Unit** (registry + build0 dispatch): `BuildForEpoch/IsSupported/ThresholdsForEpoch/VerifierPathForEpoch`
  all correct; running `verifier-build0` on the epoch-200 Addition proof
  (`7fab918b…`, `5ae97160…`, `01716c69…`) scores **74240** and passes the exact epoch-200 Addition
  threshold 74196.
- **Live end-to-end (build2, epoch 214):** a real archived solution
  (`ytwhxiplqekymcbhuwnnrdplxqzdrkugwwusurkypgtstbnphcrtpmrfuiwc`, tick 53750899) verified through
  `/api/verify` → score 76512, threshold **76430** (exact epoch-214 value), `passesThreshold:true`,
  `reconstructorVersion:"qubic-atlas-hist-1"`. Submitted via the worker path it **confirmed** through
  consensus (historical version counted).
- **Capability matching:** a `builds=build3`-only worker got `204` for a build2 job; a
  `builds=build0,build1,build2,build3` worker got it tagged `epoch=214 build=build2`.
- **Current epoch unchanged:** build3 still produces the full reconstruction (`qubic-atlas-verify-1`); the
  image smoke-test still asserts HyperIdentity score 321.
- **Backfill (ClickHouse-backed):** with `ATLAS_BACKFILL_START_TICK` at epoch 214, backfill enqueued proofs
  tick-descending and ClickHouse `verifications` filled with **epoch 214** rows (threshold 76430, confirmed,
  `qubic-atlas-hist-1`) alongside current epoch 222 — i.e. `epoch < 215` confirmed rows appeared.
- **Unsupported guard:** zero rows with `epoch ≤ 196 or > 222` after extensive enqueue/backfill;
  `IsSupported(196)==false` unit-tested; every enqueue/verify path guards on `IsSupported`.

## Deviations / notes

- **Backfill reach depends on the live `/v1/status` boundary map.** In this environment that map covers
  epochs 104–222, so backfill can classify ticks down to epoch 197. Ticks below the lowest *supported*
  boundary are skipped rather than mislabeled (the default `EpochForTickAsync` returns the latest epoch for
  out-of-range ticks, so backfill uses a **strict** in-range lookup and stops at the floor).
- **`ATLAS_BACKFILL_START_TICK`** was added (beyond the spec) so operators can target a specific historical
  range; walking organically from epoch 222 down to a build0/1/2 epoch is millions of ticks and impractical
  for a bounded run. Left unset it behaves exactly as specified (start just below the current epoch).
- **Epochs ≤196** used a different mining algorithm; such transactions typically don't even parse as
  solutions (`inputType=2, inputSize=64`), so they are doubly excluded — by shape and by the `IsSupported`
  guard. A live `epoch_unsupported` response could not be exercised because no ≤196 solution-shaped tx was
  reachable; the guard is covered by unit tests and the code paths.
- The legacy `Registry.SupportedEpochs` array is now unused (superseded by `EpochBuilds`/`IsSupported`); left
  in place to avoid unrelated churn.
