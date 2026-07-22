const base = "/api";

async function get(path) {
  const r = await fetch(base + path);
  if (!r.ok) throw new Error(`${r.status} ${path}`);
  return r.json();
}

// NOTE: API paths keep "solution" (backend contract / on-chain term). Only the UI
// renames these to "Proof".
export const api = {
  tick: () => get("/live/tick-info"),
  epochs: () => get("/epochs"),
  computors: (limit = 100, offset = 0) => get(`/computors?limit=${limit}&offset=${offset}`),
  computor: (id) => get(`/computors/${id}`),
  solutions: (params = {}) => {
    const q = new URLSearchParams(params).toString();
    return get(`/solutions?${q}`);
  },
  solution: (hash) => get(`/solutions/${hash}`),
  verify: (hash) => get(`/verify/${hash}`),
  indexStatus: () => get("/index/status"),
  // distributed verifier network
  jobsStats: () => get("/jobs/stats"),
  jobsRecent: () => get("/jobs/recent"),
  verificationsStats: () => get("/verifications/stats"),
  leaderboard: (limit = 25) => get(`/verifiers/leaderboard?limit=${limit}`),
};

export const short = (s, n = 8) => (s && s.length > 2 * n ? `${s.slice(0, n)}…${s.slice(-n)}` : s);
export const fmt = (n) => (n == null ? "—" : Number(n).toLocaleString("en-US"));

// Proof timestamps arrive as ms-since-epoch (string or number). Normalize to ms, or null.
function toMs(ts) {
  if (ts == null || ts === "") return null;
  const n = typeof ts === "string" ? Number(ts) : ts;
  return Number.isFinite(n) && n > 0 ? n : null;
}

// Absolute local date-time, e.g. "Jul 21, 2026, 14:30".
export const fmtTime = (ts) => {
  const ms = toMs(ts);
  return ms == null ? "—" : new Date(ms).toLocaleString(undefined, {
    year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit",
  });
};

// Compact relative age, e.g. "3h ago" / "2d ago" — gives a quick feel for how old a proof is.
export const timeAgo = (ts) => {
  const ms = toMs(ts);
  if (ms == null) return "";
  const s = Math.floor((Date.now() - ms) / 1000);
  if (s < 45) return "just now";
  const m = Math.floor(s / 60); if (m < 60) return `${m}m ago`;
  const h = Math.floor(m / 60); if (h < 24) return `${h}h ago`;
  const d = Math.floor(h / 24); if (d < 30) return `${d}d ago`;
  const mo = Math.floor(d / 30); if (mo < 12) return `${mo}mo ago`;
  return `${Math.floor(mo / 12)}y ago`;
};

export const FEATURED = "jfxwtxbogcsufafliioqupmnzygbytjxeucozqtmagddylxzkqhijrdblcel";

// Deep links into the official Qubic explorer.
const EXPLORER = "https://explorer.qubic.org/network";
export const explorerTick = (t) => (t == null ? null : `${EXPLORER}/tick/${t}`);
export const explorerAddr = (a) => (a ? `${EXPLORER}/address/${a}` : null);
export const explorerTx = (h) => (h ? `${EXPLORER}/tx/${h}` : null);
