# Worker authentication (signed result submissions)

Every result submitted to `POST /api/jobs/:id/result` is **cryptographically signed by the
worker** and **verified server-side**, so results are attributable and non-repudiable. The network
stays **permissionless by default** (any validly-signed identity is accepted); an optional
allowlist switches it to **permissioned mode**.

Correctness of the reconstruction itself is still decided by consensus + the server referee (see
`JobQueue.cs` / `CLICKHOUSE.md`). This layer adds **accountability + access control + a per-identity
anti-spam surface** on top of that — reputation and the `worker_results` audit trail now key on a
verified Qubic identity instead of a self-asserted string.

## Signing scheme

- **Keypair = a Qubic identity.** Signatures use **FourQ / SchnorrQ** — the same scheme Qubic uses
  on-chain — via the `Qubic.Crypto` NuGet package (`QubicCrypt.SignRaw` / `VerifyRaw`, the
  wallet-compatible "raw" convention). No Ed25519 fallback was needed; Qubic identities are used
  throughout.
- **Worker identity.** On startup the worker loads-or-generates a 55-char seed (`a-z`) from
  `ATLAS_KEY_FILE`, derives its 32-byte public key and 60-char **Qubic identity**, and uses that
  identity as its worker id. Reputation therefore keys on the identity.
- **Persistence.** The seed is persisted to `ATLAS_KEY_FILE` so identity + reputation survive
  restarts. In `docker-compose.yml` the worker mounts the named volume `worker-keys` at `/data`.

### Canonical message

The worker signs a deterministic, server-reproducible message:

```
{jobId}|{hash}|{genomeId}|{score}
```

- `jobId`   — the job id from the claim / URL path.
- `hash`    — the solution hash. **The server takes this from the trusted job**, never from the
              request body, so a client cannot sign over a hash it then changes.
- `genomeId`— the content hash of the reconstruction, computed the **identical canonical way** on
              both sides (`GenomeId.cs`; the file is shared into the worker via a linked compile).
- `score`   — the reconstruction's score (`""` when absent).

### Submission body

```jsonc
POST /api/jobs/:id/result
{
  "worker":    "<60-char Qubic identity>",
  "publicKey": "<64-hex (32-byte) public key>",   // optional; must match the identity if present
  "signature": "<128-hex (64-byte) SchnorrQ signature>",
  "reconstruction": { ... }                        // the verifier output
}
```

### Server-side verification (`WorkerAuth.cs`)

1. Recompute `genomeId` (server-side) and the canonical message from the **trusted job** + the
   submitted reconstruction.
2. Resolve the public key from the claimed identity (checksum-validated); if `publicKey` is also
   supplied it must match.
3. Verify the SchnorrQ signature over the canonical message.
4. Bind reputation + the `worker_results` audit row to the **verified** identity, and store the
   signature in the audit trail (`worker_results.signature`).

Rejection reasons (HTTP code): `signature_required` (401), `bad_signature_hex` / `bad_signature_length`
(400), `bad_identity` / `missing_identity` (400), `publickey_identity_mismatch` / `identity_mismatch`
(401), `invalid_signature` (401), `worker_not_allowlisted` (403).

## Modes

| Mode | Trigger | Behavior |
|------|---------|----------|
| **open** (default) | `ATLAS_WORKER_ALLOWLIST[_FILE]` unset | Any validly-signed identity is accepted (permissionless). |
| **allowlist** | one or more identities configured | Only listed identities' results are accepted; others get `403 worker_not_allowlisted`. |

Claiming (`GET /api/jobs/claim`) stays open in both modes — access control is enforced at result
submission. The active mode is surfaced at `GET /api/jobs/stats`:

```jsonc
{ ...queue stats..., "authMode": "open" | "allowlist", "requireSigned": true, "allowlistSize": 0 }
```

## Environment variables

### API (`api` service)

| Var | Default | Meaning |
|-----|---------|---------|
| `ATLAS_REQUIRE_SIGNED` | `true` | Reject unsigned submissions. Set `false` for backward-compat / testing (unsigned then accepted, allowlist still honored). |
| `ATLAS_WORKER_ALLOWLIST` | *(unset)* | Comma/whitespace-separated Qubic identities → permissioned mode. |
| `ATLAS_WORKER_ALLOWLIST_FILE` | *(unset)* | Path to a file of identities (one per line); merged with the above. |

### Worker (`worker` service)

| Var | Default | Meaning |
|-----|---------|---------|
| `ATLAS_KEY_FILE` | `/data/worker.key` | Seed location. Supports a `{host}` token → the container hostname, so replicas sharing one volume keep **distinct, persistent** identities (compose sets `/data/worker-{host}.key`). |
| `ATLAS_SIGN` | `true` | Set `false` to run unsigned (falls back to `WORKER_ID`; only useful against an API with `ATLAS_REQUIRE_SIGNED=false`). |
| `ATLAS_URL` | `https://qubic-atlas.org` | API base URL (compose overrides to `http://api:8099` internally). |

## Graceful degradation

- `ATLAS_REQUIRE_SIGNED=false` → unsigned submissions accepted (legacy/testing); a configured
  allowlist is still enforced against the claimed identity.
- If a worker cannot persist its key file it logs a warning and runs with an ephemeral identity.
- ClickHouse-off still works; the audit trail is simply not persisted (as before).

## Deviations from the brief

- **None on the crypto choice** — Qubic (FourQ/SchnorrQ) identities are used, not the Ed25519
  fallback.
- The default worker key path is `/data/worker.key` as specified; the compose file uses the
  `{host}` token (`/data/worker-{host}.key`) so the two replicas do not collide on a shared volume
  (required for N-of-M consensus, which needs distinct workers). Single-worker deployments can use
  the bare default.

## Verified end-to-end (`docker compose up -d --build`)

- Two workers generate distinct Qubic identities, sign, and submit → the featured HyperIdentity
  proof confirms to **321** (2-of-2, `agreed=true`); `worker_results` rows show the identities and
  128-hex (64-byte) signatures; reputation keys on the identity.
- **Tamper:** unsigned → `401 signature_required`; malformed signature → `400`; well-formed but
  wrong signature → `401 invalid_signature`.
- **Allowlist:** with one identity allowlisted, a validly-signed **non-listed** worker → `403
  worker_not_allowlisted`; the **listed** worker → accepted.
- `GET /api/jobs/stats` reports `authMode` + `requireSigned` + `allowlistSize`.
