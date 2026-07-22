# Qubic Atlas — Verifier Worker

**Join the decentralized verification network for Qubic ANN mining proofs with one command.**

Qubic miners don't hash — they train tiny neural networks. [Qubic Atlas](https://qubic-atlas.org) re-runs the **canonical Qubic Core scorer** on the public, on-chain inputs of each mining proof and reconstructs the neural network, so results are *reproduced*, never trusted. This image is a **community worker**: it claims proofs from the network, re-scores them with the era-correct scorer, signs the result with a Qubic identity, and submits it. Proofs are **confirmed** only when independent workers agree.

## Quick start

```bash
docker run -d --name atlas-worker --restart unless-stopped qubiclab/atlas-worker:latest
```

That's it. Zero config: the public coordinator URL is baked in, and the worker generates and persists its own Qubic identity on first run. It immediately begins verifying the latest proofs and, when it has spare capacity, works backward through history.

### Persist your identity (recommended)

Your worker's Qubic identity is how it earns reputation on the [contributor leaderboard](https://qubic-atlas.org/network) — and a payable address if the community later tips contributors. Mount a volume so the key survives restarts:

```bash
docker run -d --name atlas-worker --restart unless-stopped \
  -v atlas-worker-keys:/data \
  qubiclab/atlas-worker:latest
```

### Bring your own identity

```bash
docker run -d --restart unless-stopped \
  -e ATLAS_SEED=your55characterlowercasequbicseedaaaaaaaaaaaaaaaaaaaa \
  qubiclab/atlas-worker:latest
```

### Run several workers

More workers = faster confirmation and more history covered. Each replica gets a distinct identity when it has its own key file:

```bash
docker run -d --restart unless-stopped -v atlas-w1:/data qubiclab/atlas-worker:latest
docker run -d --restart unless-stopped -v atlas-w2:/data qubiclab/atlas-worker:latest
```

## Configuration

| Env var | Default | Purpose |
|---|---|---|
| `ATLAS_URL` | *(public coordinator, baked in)* | Override to point at a private Atlas instance |
| `ATLAS_SEED` | *(auto-generated)* | 55-char lowercase Qubic seed — bring your own identity |
| `ATLAS_KEY_FILE` | `/data/worker-{host}.key` | Where the generated identity is persisted |
| `ATLAS_SIGN` | `true` | Sign submissions (leave on; the network requires signed results) |
| `ATLAS_BUILDS` | *(auto-probed)* | Which era-binaries to advertise (`build0,build1,build2,build3`) |
| `POLL_INTERVAL_MS` | `3000` | Idle poll interval |

## What it runs

The image bundles **four era-specific verifier binaries**, each reproducing its epochs byte-exact (Qubic Core v1.275 → v1.299, epochs 197 → current). The coordinator tells each worker which build a proof needs; the worker only claims jobs it can run. Each verification needs ~512 MB RAM and takes ~1–3 s. **AVX2 only** — runs on any x86-64 CPU since ~2013; no AVX-512 required.

## Automatic updates

Run `:latest` and you stay current. When Qubic ships a **new mining algorithm** (e.g. a future BPP/ant-colony variant), we publish an updated `:latest` that adds the new verifier — pull it and your worker supports the new proofs with no reconfiguration.

```bash
docker pull qubiclab/atlas-worker:latest && docker restart atlas-worker
```

## Links

- 🌐 Web app: **https://qubic-atlas.org**
- 📖 How it works / docs: **https://qubic-atlas.org/docs**
- 💻 Source (open source): **https://github.com/qubic-atlas/core**
- 🧩 Qubic: [qubic.org](https://qubic.org) · [docs.qubic.org](https://docs.qubic.org) · [Discord](https://join.qubic.org/discord) · [X](https://x.com/_qubic_)

---
*Not affiliated with the Qubic core team. Qubic Atlas verifies public on-chain data independently.*
