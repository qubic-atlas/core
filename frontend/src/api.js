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

export const FEATURED = "jfxwtxbogcsufafliioqupmnzygbytjxeucozqtmagddylxzkqhijrdblcel";
