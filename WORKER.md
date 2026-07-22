# Run a Qubic Atlas Verifier

Qubic Atlas verifies mining Proofs on a **volunteer network**: anyone can run a *verifier worker*
that re-computes Proofs with the canonical Qubic Core scorer and reports the results back. The more
workers, the faster the whole archive gets independently confirmed.

**It's safe to open this to anyone.** The scorer is deterministic, so every Proof has exactly one
correct answer. A worker that returns a wrong result can't corrupt anything — workers must agree on
the **genome hash**, conflicts are settled by a **referee re-compute**, and bad workers simply lose
reputation and get excluded. See [The consensus model](#why-its-safe-to-let-anyone-join).

## Quick start (Docker — recommended)

One command. The verifier binary and the default Atlas URL (`https://qubic-atlas.org`) are baked into
the image, and the worker only makes **outbound** HTTPS calls (claims jobs, submits results), so it
works from home / behind NAT with no port forwarding — and you don't pass any config.

```bash
docker run -d --restart unless-stopped --name atlas-worker qubiclab/atlas-worker:latest
```

_(Self-hosting a different Atlas? Override with `-e ATLAS_URL=https://your-host`.)_

That's it — it starts claiming and verifying Proofs. Check it's working:

```bash
docker logs -f atlas-worker
# then watch your worker appear on https://qubic-atlas.org/network
```

### Run several workers on one machine

Each worker verifies one Proof at a time; run several to use more cores:

```bash
# worker-quickstart/docker-compose.yml
docker compose -f worker-quickstart/docker-compose.yml up -d --scale worker=4
```

## Configuration (env vars)

| Var | Default | Meaning |
|-----|---------|---------|
| `ATLAS_URL` | `https://qubic-atlas.org` (baked in) | The Atlas API to connect to. |
| `ATLAS_SEED` | — | Explicit 55-char Qubic seed (bring-your-own identity). Highest precedence. Prefer a mounted secret over env. |
| `ATLAS_KEY_FILE` | `/data/worker.key` | File holding the seed (e.g. a Docker secret). Read if `ATLAS_SEED` unset; auto-generated here on first run if absent. |
| `POLL_INTERVAL_MS` | `3000` | How often to ask for a job when idle. |
| `VERIFIER` | `/usr/local/bin/verifier` | Path to the scorer binary (baked into the image). |

Your **worker id is your Qubic identity**, derived from the seed — reputation and leaderboard rank key on it.

## Your verifier identity (the seed)

The seed is a **55-character Qubic seed** — a private key that *also controls a payable Qubic address*.
The worker resolves it in this order:

1. **`ATLAS_SEED`** — an explicit seed you supply (your wallet identity). Fail-fast if malformed.
2. **`ATLAS_KEY_FILE`** — a file/secret holding the seed.
3. **Auto-generate** (default) — a fresh seed is minted and persisted to `ATLAS_KEY_FILE` on first run.

**Zero-config (default):** just run the image — you get a persistent auto-generated identity in the
`/data` volume. Note it's a *burner*: back up `/data/worker.key` if you want to keep your identity, rank,
and any tips.

**Bring your own identity (for recognition / donations):** supply your own Qubic wallet seed so rank and
tips accrue to an address you control. Use a **Docker secret**, never bake it into an image:

```yaml
services:
  worker:
    image: qubiclab/atlas-worker:latest
    secrets: [atlas_seed]
    environment: { ATLAS_KEY_FILE: /run/secrets/atlas_seed }
secrets:
  atlas_seed:
    file: ./my-atlas-seed.txt   # a file containing your 55-char seed, perms 600
```

The worker logs only the **derived identity** on startup (never the seed).

## Requirements

- **Docker**, outbound HTTPS. No inbound ports, no account, no stake.
- **~1 GB free RAM** — each verification allocates a ~512 MB deterministic pool.
- Throughput per worker: **Addition** Proofs < 1 s each; **HyperIdentity** ~3 s each (1000 ticks).

## Build from source (for auditors)

You don't have to trust our image — build the exact same deterministic binary yourself. Because the
result is byte-exact, a worker you built matches everyone else's:

```bash
git clone <atlas-repo> && cd qubic-atlas
docker build -t atlas-verifier ./scorer          # C++ scorer; self-tests score 321 during build
docker build -t atlas-worker -f backend/AtlasWorker/Dockerfile backend
docker run -d --restart unless-stopped -e ATLAS_URL=https://qubic-atlas.org atlas-worker
```

Non-Docker: `scorer/build.sh` (needs clang) produces the `verifier` binary; then
`dotnet run --project backend/AtlasWorker` with `ATLAS_URL` set and `VERIFIER` pointing at it.

## Why it's safe to let anyone join

- **Deterministic** — identical public inputs + the canonical Core scorer ⇒ a byte-identical
  `(score, genome)`. There is one correct answer per Proof.
- **Consensus on the genome hash** — a Proof is *confirmed* only when the required number of workers
  independently produce the **same** `sha256` genome. A tampered worker would have to forge an entire
  self-consistent network hashing to the same value for a wrong score — infeasible.
- **Referee resolution** — on any disagreement, the coordinator re-computes with its own trusted
  verifier; that answer is authoritative (anyone can re-derive it). The dissenting worker is flagged.
- **Reputation** — workers that repeatedly disagree with the referee lose reputation and stop being
  given jobs. No stake to lose; participation is permissionless, influence is earned.
- **Version pinning** — results are tagged with the verifier version; off-version results don't count
  toward confirmation.

Every submission (including rejected ones) is kept in an audit trail, so the network is fully
transparent about who reported what.

---

### For operators: publishing the worker image

Community users pull `qubiclab/atlas-worker:latest` (or your Docker Hub equivalent). To publish:

```bash
docker build -t qubiclab/atlas-worker:latest -f backend/AtlasWorker/Dockerfile backend
docker push qubiclab/atlas-worker:latest
```

You also need the Atlas **API reachable at a public URL** (`ATLAS_URL`) with CORS/ratelimit as
appropriate. The API treats all worker input as untrusted claims — consensus + referee handle safety —
but add basic rate limiting on `/api/jobs/*` to blunt spam.
