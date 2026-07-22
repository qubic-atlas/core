# Introducing Qubic Atlas

**A decentralized verification explorer for Qubic's AI proof-of-work — now in public beta at [qubic-atlas.org](https://qubic-atlas.org).**

---

## The short version

Qubic's miners don't hash — they **train tiny neural networks**, and the network rewards the best ones. That's what makes Qubic's proof-of-work *useful*. But until now there hasn't been an open, independent way to **see and verify** what those miners actually computed.

**Qubic Atlas** does exactly that. For every mining proof, it re-runs the **canonical Qubic Core scorer** on the public on-chain inputs, reconstructs the full neural network — score, neurons, synapses, and the mutation search that built it — and confirms the result through a **distributed network of independent verifiers**. Nothing is trusted; everything is recomputed. If the numbers reproduce, the proof is real, and anyone can check it themselves.

## Why it matters

- **Trustless by construction.** Every input needed to score a proof is public and on-chain. Atlas is the *actual* Core scorer compiled unmodified, so its output is bit-identical to what computors run. Genomes are content-addressed, so two independent verifiers anywhere in the world produce the same hash for the same proof — they cross-check each other with zero trust. Not one oracle; a reproducible computation anyone can run.
- **A real community network, not a single server.** Anyone can contribute verification power with one command. Submissions are cryptographically signed with a Qubic identity, proofs are confirmed only when independent workers agree, conflicts are settled by re-derivation, and a contributor leaderboard tracks who's doing the work.
- **Transparency for the whole ecosystem.** Miners, the community, researchers, and partners get an open window into what Qubic's PoW is producing — verifiable, not asserted.

## What's live today (beta)

- Full, independent re-scoring and neural-network reconstruction for the current mining algorithm.
- **Backward compatibility across ~26 epochs** (epoch 197 onward), each verified byte-exact with the era-correct scorer.
- A distributed verifier network with signed submissions, N-of-M consensus, referee conflict resolution, and reputation.
- Rich visualizations of each trained network (flow, radial, and weight-matrix views).
- Multi-language UI: English, German, Spanish, French, Chinese.
- **Fully open source.**

## Run a verifier (join the network)

```
docker run -d qubiclab/atlas-worker:latest
```

Zero config. It starts verifying the latest proofs immediately and works backward through history when it has spare capacity.

## On the roadmap

- **Future mining algorithms, automatically.** When Qubic adopts a new proof-of-work, workers on `:latest` pull the update and verify the new proofs with no reconfiguration.
- **A free, public results API** so anyone can build on Atlas's verified data.
- In-browser (WebAssembly) verification, multi-architecture worker images, and contributor rewards.

## Links

- **Explore:** https://qubic-atlas.org
- **How it works:** https://qubic-atlas.org/docs
- **Source (open source):** https://github.com/qubic-atlas/core
- **Roadmap:** https://github.com/qubic-atlas/core/blob/main/ROADMAP.md

Feedback very welcome — this is a beta, and the direction is shaped by the community.

*Qubic Atlas verifies public on-chain data independently.*

---

## Short version (Discord / Telegram)

> **🛰️ Introducing Qubic Atlas — public beta**
>
> Qubic miners train neural networks; Atlas lets anyone **independently verify** them. For every mining proof it re-runs the canonical Qubic Core scorer on public on-chain data, reconstructs the full network, and confirms it through a distributed network of verifiers. Zero trust — everything is recomputed.
>
> ✅ Verifies ~26 epochs byte-exact · signed submissions + consensus · open source · EN/DE/ES/FR/ZH
>
> Join the network with one command:
> `docker run -d qubiclab/atlas-worker:latest`
>
> 🌐 https://qubic-atlas.org · 💻 https://github.com/qubic-atlas/core

## One-liner (X / headline)

> Qubic Atlas (beta): a decentralized explorer that independently **re-verifies** Qubic's AI proof-of-work — re-running the canonical scorer on public on-chain data, confirmed by a network of independent verifiers. Zero trust, fully open source. → qubic-atlas.org
