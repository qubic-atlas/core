# Qubic Atlas — API & naming contract (source of truth for the C#/Vue port)

Product name: **Qubic Atlas**. In the **UI**, a mining "solution" is called a **Proof** (each is one
neural network a miner trained). **API paths keep `solution`** (matches Qubic's on-chain term and the
reference Node server) — only UI copy uses "Proof"/"Atlas". The network's canonical encoding is its
**genome** (`annGenomeId`, a content hash); newcomer copy may call it "the trained network".

This document was the porting contract against an original Node/React reference (since removed).
The **implementation** is now the ASP.NET Core service in `backend/` + the Vue 3 SPA in `frontend/`,
which this spec describes; it remains the source of truth for API shapes and naming.

## Verifier CLI (C++ binary, unchanged)
`verifier <miningSeedHex> <pubKeyHex> <nonceHex> <expectedScore|-1>` → prints reconstruction JSON to stdout.
- nonce[0] parity: even ⇒ HyperIdentity, odd ⇒ Addition.
- ~512MB RAM per run; HyperIdentity ≈ 3s, Addition < 1s.

## Data shapes
**Proof** (list/detail):
```
{ hash, algorithm: "HyperIdentity"|"Addition", computorId, computorPublicKey, miningSeed, nonce,
  tickNumber, timestamp, epoch, threshold, coreVersion, scoreRule, verification: { status: "unverified" } }
```
**Reconstruction** (verifier output + server enrichment):
```
{ reconstructorVersion, algorithm, score, threshold, passesThreshold, rawScoreValid,
  metrics: { inputNeurons, outputNeurons, ticks, population, populationThreshold, neighbors,
             evolutionNeurons, mutations, executedMutations, acceptedMutations, rejectedMutations,
             nonzeroSynapses, positiveSynapses, negativeSynapses, zeroSynapses, neighborSlots, synapseDensity },
  graph: { nodes: [{id,type:"input"|"output"|"evolution",value}], links: [{source,target,weight}],
           matrix: { g:56, cells:[int...] }, renderedLinks, totalLinks, truncatedLinks },
  mutationTrace: { totalSteps, events: [{step,bestScore,candidateScore,accepted,populationBefore,populationAfter}] },
  // server adds: elapsedMs, solutionHash, epoch, coreVersion, paramSetId, computorId, tickNumber,
  //              inputs:{miningSeed,computorPublicKey,nonce}, source:"public-archive", verifiedLocally:true }
```

## HTTP endpoints (keep paths identical to the Node reference)
- `GET /api/live/tick-info` → `{ tickInfo:{ tick, epoch, initialTick, duration } }` (proxy `${RPC}/v1/tick-info`)
- `GET /api/solutions?limit&offset&algorithm&epoch` → `{ items:[Proof], hasMore, source:"index"|"firehose", indexed }`
- `GET /api/solutions/:hash` → Proof
- `GET /api/ticks/:tick/solutions` → `{ items:[Proof], tick }`
- `GET /api/epochs` → `{ items:[{epoch,coreVersion,algoFamily,firstTick,lastTick,solutions,verification}] }`
- `GET /api/computors` → `{ items:[{rank,computorId,solutions,firstTick,lastTick}], total, window, source }`
- `GET /api/verify/:hash` → Reconstruction (fetch inputs from archive, run verifier, cache by hash on disk)
- `GET /api/index/status` → `{ indexedSolutions, newestTick, oldestTick }`
- `GET /api/health` → `{ ok, rpc, solutionDest, verifier, relay:"none (public archive only)" }`

### Distributed verifier network (job API)
- `POST /api/jobs/enqueue` `{hash}` → job
- `POST /api/jobs/seed?n=5` → enqueue N recent unverified proofs
- `GET  /api/jobs/claim?worker=ID` → `{ jobId, hash, algorithm, miningSeed, computorPublicKey, nonce, threshold }` or 204
- `POST /api/jobs/:id/result` `{ worker, reconstruction }` → `{ done, confirmations }` (on done, write reconstruction to the verify cache)
- `GET  /api/jobs/stats` → `{ pending, leased, done, total, requiredConfirmations, workers:[{id,completed,online}], workersOnline }`
- `GET  /api/jobs/recent` → recent jobs
Consensus: a proof is **confirmed** when `requiredConfirmations` workers agree on the score (default 1; design supports N-of-M on the genome hash).

## Qubic specifics (port from Node rpc.js)
- Solutions are on-chain txs: destination `AAAAAAAA…FXIB`, inputType 2, inputSize 64, input = miningSeed(32)‖nonce(32), source = computor identity.
- Firehose: `${RPC}/v2/identities/{DEST}/transactions?desc=true&page=N` (field `input` base64).
- By tick: `${RPC}/v1/ticks/{tick}/transactions` (field `inputHex`).
- identity→pubkey: **use Qubic.Crypto** (QubicCrypt) in C#; else base26 (4×8-byte little-endian frags, 14 chars each).
- Params (epoch 222 / v1.299.1): HyperIdentity thr 316, Addition thr 74100. See backend/Registry.cs.

## Env / config
`QUBIC_RPC` (default https://rpc.qubic.org), `PORT` (8099), `ATLAS_CONFIRMATIONS` (1),
verifier binary path, cache dir, index dir.
