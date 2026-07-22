# Qubic Atlas — Verifier

The decentralized verifier for [Qubic Atlas](https://qubic-atlas.org): the **canonical Qubic Core ANN scorer**, compiled unmodified for Linux and instrumented to emit reconstruction JSON (score, neuron/synapse graph, structural metrics, mutation trace). This image bundles **four era-specific binaries** — one per Core release span — so any epoch from **197 onward** (Core v1.275 → v1.299) reproduces **byte-exact**. It self-tests to the known score 321 during build.

You usually don't run this alone — it's baked into `qubiclab/atlas-api` and `qubiclab/atlas-worker`. To score a proof directly:

```bash
docker run --rm qubiclab/atlas-verifier:latest \
  verifier <miningSeedHex> <computorPubKeyHex> <nonceHex> -1
```

**AVX2 only** — runs on any x86-64 CPU since ~2013; ~512 MB RAM per run. No AVX-512 required.

- 🌐 https://qubic-atlas.org · 📖 https://qubic-atlas.org/docs · 💻 https://github.com/qubic-atlas/core

*Not affiliated with the Qubic core team. Verifies public on-chain data independently.*
