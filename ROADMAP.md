# Qubic Atlas — Roadmap

Qubic Atlas is under active development (it's currently in **beta**). This is where we're headed. Priorities shift with community input — [open an issue](https://github.com/qubic-atlas/core/issues) or reach us on the [Qubic Discord](https://join.qubic.org/discord).

## Future mining algorithms (auto-updating workers)

Qubic's proof-of-useful-work evolves. When the network adopts a **new mining algorithm** — a future BPP/"BPP9000"-style scorer, an ant-colony / swarm variant, or anything that succeeds `dual_hyperidentity_addition` — Qubic Atlas will add the matching verifier build.

**Community workers get this automatically.** Anyone running `qubiclab/atlas-worker:latest` just pulls the new image and immediately begins verifying proofs under the new algorithm — no reconfiguration, no manual per-epoch setup. The coordinator advertises which build each proof needs, and the worker runs the era-correct binary. This is the core promise: **one command, always current with Qubic's PoW.**

```bash
docker pull qubiclab/atlas-worker:latest && docker restart atlas-worker
```

## Public, free results API

A documented, **free, public REST API** so anyone can consume Atlas's verified results — dashboards, research, block explorers, mining pools, bots.

- Stable read endpoints: proofs, per-epoch stats, computor summaries, the contributor leaderboard, and per-proof reconstructions (score, genome graph, metrics, mutation trace).
- OpenAPI/Swagger spec + examples; content-addressed `annGenomeId` for cross-referencing.
- Optional webhooks / streaming for "proof confirmed" events.
- Generous anonymous rate limits; no key required for read access.

## Other planned work

- **Full historical coverage** — extend verification below epoch 197 (the earlier mining algorithm) if there's community demand.
- **In-browser verification (WebAssembly)** — the scorer is AVX2 (not AVX-512), so a portable build can run *in the browser tab*, making the page itself a trustless verifier with no install.
- **Multi-arch images** — publish `linux/arm64` alongside `amd64` so workers run on Raspberry Pi / ARM servers / Apple Silicon.
- **Versioned image tags + signed releases** — semver tags beside `:latest`, image signing, and a public changelog.
- **Contributor rewards** — each worker is a payable Qubic identity; enable community tipping/donations for high-contribution verifiers, surfaced on the leaderboard.
- **Public status & metrics** — network throughput, confirmation latency, worker count, coverage per epoch; a status page and Prometheus metrics.
- **More languages** — the UI ships in English, German, Spanish, French, and Chinese; more can be added easily (single message catalog).
- **Deeper analytics** — search/filter by computor, genome similarity, score distributions over time, and epoch-over-epoch trends.
- **Data export** — CSV/Parquet dumps of verified results for researchers.

## Done (recent)

- ✅ Multi-epoch backward compatibility — byte-exact verification for epochs 197–current across four era builds.
- ✅ Distributed verifier network — signed submissions, N-of-M consensus, referee conflict resolution, reputation.
- ✅ Standalone .NET backend + ClickHouse persistence; RPC caching + resilience hardening.
- ✅ Multi-language UI; beta banner; official Qubic links.

---
*Not affiliated with the Qubic core team. Qubic Atlas verifies public on-chain data independently.*
