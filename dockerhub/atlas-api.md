# Qubic Atlas — API

Backend for [Qubic Atlas](https://qubic-atlas.org), the decentralized verification explorer for Qubic ANN mining proofs. ASP.NET Core service that ingests proofs from the public Qubic archive, coordinates the distributed verifier network (claim → signed result → N-of-M consensus → referee), and persists verdicts to ClickHouse. The era-correct C++ scorer binaries are baked in for on-demand verification. Standalone — no external indexer.

Most people don't run this directly — you run a **worker** (`qubiclab/atlas-worker`) or use the hosted app. Self-hosters use the full stack:

```bash
# see the deploy compose in the repo (api + frontend + clickhouse + workers + cloudflared)
docker compose up -d
```

**Config:** `CLICKHOUSE_URL`, `CLICKHOUSE_USER`, `CLICKHOUSE_PASSWORD`, `QUBIC_RPC`, `ATLAS_CONFIRMATIONS`, `ATLAS_REQUIRE_SIGNED`. Health + cache stats at `/api/health`.

- 🌐 https://qubic-atlas.org · 📖 https://qubic-atlas.org/docs · 💻 https://github.com/qubic-atlas/core

*Not affiliated with the Qubic core team. Verifies public on-chain data independently.*
