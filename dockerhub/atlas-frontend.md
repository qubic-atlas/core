# Qubic Atlas — Frontend

Web UI for [Qubic Atlas](https://qubic-atlas.org), the decentralized verification explorer for Qubic ANN mining proofs. A Vue 3 single-page app served by nginx, which also reverse-proxies `/api` to the Atlas API. Browse proofs, reconstruct and visualize each trained neural network (flow / radial / weight-matrix views), inspect epochs and computors, and watch the verifier network. Multi-language (English, German, Spanish, French, Chinese).

```bash
docker run -d -p 8080:80 -e ATLAS_UPSTREAM=api:8099 qubiclab/atlas-frontend:latest
```

**Config:** `ATLAS_UPSTREAM` — the `host:port` the `/api` proxy forwards to (the Atlas API). Typically run via the deploy compose in the repo.

- 🌐 https://qubic-atlas.org · 📖 https://qubic-atlas.org/docs · 💻 https://github.com/qubic-atlas/core

*Not affiliated with the Qubic core team. Verifies public on-chain data independently.*
