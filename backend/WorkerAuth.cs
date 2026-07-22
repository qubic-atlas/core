using System.Text;
using Qubic.Crypto;

namespace QubicAtlas;

// Server-side authentication for worker result submissions.
//
// Every submission to /api/jobs/{id}/result is cryptographically signed by the worker's Qubic
// keypair (FourQ / SchnorrQ, the same scheme Qubic uses on-chain). The server recomputes the
// canonical signed message from the TRUSTED job (its hash) plus the server-computed genome_id and
// the submitted score, then verifies the signature against the claimed identity. This makes every
// result attributable and non-repudiable — reputation and the worker_results audit trail bind to
// the VERIFIED identity, not to a self-asserted "worker" string.
//
// The network stays permissionless by default (open mode: any validly-signed identity is accepted).
// Setting ATLAS_WORKER_ALLOWLIST[_FILE] switches to permissioned mode: only listed identities pass.
// Correctness of the reconstruction itself is still decided by consensus/referee — this layer only
// adds accountability + access control + a per-identity anti-spam surface.
public sealed class WorkerAuth
{
    private readonly IQubicCrypt _crypt = new QubicCrypt();
    public bool RequireSigned { get; }
    private readonly HashSet<string> _allowlist;   // empty => open (permissionless) mode

    public WorkerAuth(bool requireSigned, IEnumerable<string> allowlist)
    {
        RequireSigned = requireSigned;
        _allowlist = new HashSet<string>(
            allowlist.Select(Norm).Where(s => s.Length == 60), StringComparer.Ordinal);
    }

    public string Mode => _allowlist.Count > 0 ? "allowlist" : "open";
    public int AllowlistSize => _allowlist.Count;

    private static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

    // The canonical, deterministic message a worker signs. Reproducible byte-for-byte on the
    // server from trusted inputs. hash comes from the job (never the client); genomeId is computed
    // server-side from the submitted reconstruction; score is the reconstruction's score.
    public static string CanonicalMessage(string jobId, string hash, string genomeId, long? score)
        => $"{jobId}|{hash}|{genomeId}|{(score?.ToString() ?? "")}";

    public sealed record Result(bool Ok, int Code, string? Error, string Identity, bool Signed);

    // Verify a submission. `worker` is the claimed 60-char Qubic identity; publicKeyHex/signatureHex
    // are optional hex strings; `message` is the canonical message recomputed server-side.
    public Result Check(string? worker, string? publicKeyHex, string? signatureHex, string message)
    {
        string claimed = Norm(worker);
        bool hasSig = !string.IsNullOrWhiteSpace(signatureHex);

        // ---- unsigned submission ----
        if (!hasSig)
        {
            if (RequireSigned)
                return new Result(false, 401, "signature_required", claimed, false);
            // Backward-compat / testing: accept unsigned, but still honor an allowlist if set.
            if (_allowlist.Count > 0 && !_allowlist.Contains(claimed))
                return new Result(false, 403, "worker_not_allowlisted", claimed, false);
            return new Result(true, 200, null, claimed.Length > 0 ? claimed : "anon", false);
        }

        // ---- signed submission: parse the signature ----
        byte[] sig;
        try { sig = Convert.FromHexString(signatureHex!); }
        catch { return new Result(false, 400, "bad_signature_hex", claimed, true); }
        if (sig.Length != 64) return new Result(false, 400, "bad_signature_length", claimed, true);

        // ---- resolve the 32-byte public key ----
        // Prefer the claimed identity (self-describing + checksummed). If a publicKey hex is also
        // supplied it MUST match the identity's key. Fall back to the supplied publicKey hex.
        byte[] pk;
        if (claimed.Length == 60 && _crypt.VerifyIdentityChecksum(claimed))
        {
            try { pk = _crypt.GetPublicKeyFromIdentity(claimed); }
            catch { return new Result(false, 400, "bad_identity", claimed, true); }
            if (!string.IsNullOrWhiteSpace(publicKeyHex))
            {
                byte[] declared;
                try { declared = Convert.FromHexString(publicKeyHex!); }
                catch { return new Result(false, 400, "bad_publickey_hex", claimed, true); }
                if (declared.Length != 32 || !declared.SequenceEqual(pk))
                    return new Result(false, 401, "publickey_identity_mismatch", claimed, true);
            }
        }
        else if (!string.IsNullOrWhiteSpace(publicKeyHex))
        {
            try { pk = Convert.FromHexString(publicKeyHex!); }
            catch { return new Result(false, 400, "bad_publickey_hex", claimed, true); }
            if (pk.Length != 32) return new Result(false, 400, "bad_publickey_length", claimed, true);
        }
        else
        {
            return new Result(false, 400, "missing_identity", claimed, true);
        }

        // Canonical identity is derived from the verified public key (checksum-normalized).
        string identity = _crypt.GetIdentityFromPublicKey(pk);
        if (claimed.Length == 60 && !string.Equals(identity, claimed, StringComparison.Ordinal))
            return new Result(false, 401, "identity_mismatch", claimed, true);

        // ---- verify the SchnorrQ signature over the canonical message ----
        if (!_crypt.VerifyRaw(pk, Encoding.UTF8.GetBytes(message), sig))
            return new Result(false, 401, "invalid_signature", identity, true);

        // ---- allowlist gate (permissioned mode) ----
        if (_allowlist.Count > 0 && !_allowlist.Contains(identity))
            return new Result(false, 403, "worker_not_allowlisted", identity, true);

        return new Result(true, 200, null, identity, true);
    }

    // Build an allowlist from a comma/whitespace-separated env value and/or a file (one per line).
    public static List<string> LoadAllowlist(string? csv, string? file)
    {
        var items = new List<string>();
        void AddAll(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            foreach (var part in s.Split(new[] { ',', '\n', '\r', ' ', '\t' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                items.Add(part);
        }
        AddAll(csv);
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            try { AddAll(File.ReadAllText(file)); }
            catch (Exception e) { Console.WriteLine($"[auth] failed to read allowlist file {file}: {e.Message}"); }
        }
        return items;
    }
}
