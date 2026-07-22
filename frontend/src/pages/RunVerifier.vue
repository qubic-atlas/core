<script setup>
// Commands as strings so newlines survive Vue's template whitespace-condensing
// (the .codeblock CSS renders them with white-space: pre).
const cmdDocker = `docker run -d --restart unless-stopped --name atlas-worker qubiclab/atlas-worker:latest`;
const cmdScale = `docker compose -f worker-quickstart/docker-compose.yml up -d --scale worker=4`;
const cmdBuild = `# C++ scorer — self-tests score 321 during the build
docker build -t atlas-verifier ./scorer
docker build -t atlas-worker -f backend/AtlasWorker/Dockerfile backend
docker run -d --restart unless-stopped atlas-worker`;
</script>

<template>
  <p class="sub"><router-link to="/network">← Verifier Network</router-link></p>
  <h1>Run a verifier</h1>
  <p class="sub">Join the network: re-compute Qubic Proofs with the canonical Core scorer and help confirm the archive. Permissionless — no account, no stake.</p>

  <div class="banner ok banner--info">
    <b>One command, no config.</b> The scorer and the Atlas URL (<span class="mono">https://qubic-atlas.org</span>) are
    baked into the image, and the worker only makes <b>outbound</b> calls (it asks for jobs and submits results),
    so it runs from home or behind NAT with no port forwarding.
  </div>

  <h3>Docker (recommended)</h3>
  <div class="codeblock">{{ cmdDocker }}</div>
  <p class="muted">Then watch your worker appear on the <router-link to="/network">Network</router-link> page. Logs: <span class="mono">docker logs -f atlas-worker</span></p>

  <h3>Run several at once</h3>
  <div class="codeblock">{{ cmdScale }}</div>

  <h3>Build it yourself (auditors)</h3>
  <p class="muted">You don't have to trust our image. Because the result is byte-exact, a worker you build matches everyone else's:</p>
  <div class="codeblock">{{ cmdBuild }}</div>

  <div class="grid2">
    <div class="panel pad">
      <h3 class="panel-h">Requirements</h3>
      <ul class="muted">
        <li>Docker + outbound HTTPS. No inbound ports, no account, no stake.</li>
        <li>~1 GB free RAM (each check allocates a ~512 MB deterministic pool).</li>
        <li>Throughput: Addition Proofs &lt; 1 s, HyperIdentity ~3 s each.</li>
      </ul>
    </div>
    <div class="panel pad">
      <h3 class="panel-h">Config</h3>
      <ul class="muted">
        <li><span class="mono">ATLAS_URL</span> — the Atlas API to connect to.</li>
        <li><span class="mono">WORKER_ID</span> — auto-generated; set for a stable name.</li>
        <li><span class="mono">POLL_INTERVAL_MS</span> — idle poll rate (default 3000).</li>
      </ul>
    </div>
  </div>

  <h3>Your verifier identity</h3>
  <p class="muted">
    Each worker is a <b>Qubic identity</b> — reputation and leaderboard rank key on it, and since it's a
    payable address, tips can go to it directly. By default the worker <b>auto-generates</b> a persistent
    identity in its <span class="mono">/data</span> volume (a burner — back it up to keep it). To earn
    recognition/donations on an address you already control, <b>bring your own seed</b> via a Docker secret
    (<span class="mono">ATLAS_KEY_FILE=/run/secrets/atlas_seed</span>) or <span class="mono">ATLAS_SEED</span>.
    Never bake a seed into an image. See <span class="mono">WORKER.md</span> for the compose snippet.
  </p>

  <h3>Why it's safe to let anyone join</h3>
  <p class="muted">
    The scorer is <b>deterministic</b> — one correct genome per Proof. Workers must agree on the
    <b>genome hash</b> to confirm it; disagreements are settled by a <b>referee re-compute</b> (authoritative,
    since anyone can re-derive it); dissenting workers lose reputation and get excluded. A malicious worker
    can't corrupt a verdict — only get filtered out. Every submission is kept in an audit trail.
  </p>
</template>
