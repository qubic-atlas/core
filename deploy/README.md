# Deploy Qubic Atlas (Docker Hub + Cloudflare Tunnel)

Runs the full stack from published images (`qubiclab/atlas-*`), served **only** through a
Cloudflare Tunnel — no ports are exposed on the host.

## Prerequisites
- Docker + Docker Compose.
- A Cloudflare account with `qubic-atlas.org` on Cloudflare, and Zero Trust enabled.
- CPU: x86-64 with **AVX2** (any Intel/AMD since ~2013). No AVX-512 needed.

## 1. Create the tunnel
In **Cloudflare Zero Trust → Networks → Tunnels**:
1. Create a tunnel (e.g. `qubic-atlas`). Copy its **connector token**.
2. Add a **Public Hostname**: `qubic-atlas.org` → Service **`http://frontend:80`**.
   (cloudflared runs on the compose network and resolves the `frontend` service by name.)

## 2. Configure
```bash
cd deploy
cp .env.example .env
# paste the token:  CF_TUNNEL_TOKEN=...
```

## 3. Run
```bash
docker compose up -d
docker compose up -d --scale worker=4     # more verifier workers
docker compose logs -f cloudflared        # confirm the tunnel is connected
```

Open `https://qubic-atlas.org`. Nothing else is reachable from the internet — `api`,
`clickhouse`, and `frontend` have no published ports; only `cloudflared` egresses to Cloudflare.

## Notes
- **Images**: published by the CI workflow (`.github/workflows/publish-images.yml`) to
  `qubiclab/atlas-{api,worker,frontend}`; the verifier binary is baked into the api + worker images.
- **Persistence**: named volumes `clickhouse-data` (verifications/audit), `api-cache` (regenerable
  reconstructions), `worker-keys` (per-replica signing identities).
- **Permissioned mode**: set `ATLAS_WORKER_ALLOWLIST` on `api` to a comma-separated list of Qubic
  identities to accept results only from approved verifiers.
- **Updating**: `docker compose pull && docker compose up -d`.
