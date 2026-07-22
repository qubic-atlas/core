<script setup>
import { t } from "../i18n.js";
// Commands as strings so newlines survive Vue's template whitespace-condensing
// (the .codeblock CSS renders them with white-space: pre).
const cmdDocker = `docker run -d --restart unless-stopped --name atlas-worker qubiclab/atlas-worker:latest`;
// Full compose sample — a fixed fleet where each replica keeps its own persistent identity.
const cmdCompose = `# docker-compose.yml — N verifier workers, each a distinct, persistent identity
services:
  worker:
    image: qubiclab/atlas-worker:latest
    environment:
      # {host} expands to the container id, so replicas sharing the volume below
      # still get distinct, persisted identities (needed for N-of-M consensus).
      ATLAS_KEY_FILE: /data/worker-{host}.key
    volumes:
      - worker-keys:/data
    restart: unless-stopped
    deploy:
      replicas: \${WORKER_REPLICAS:-4}   # override without editing: WORKER_REPLICAS=8

volumes:
  worker-keys:`;
const cmdComposeUp = `docker compose up -d              # start the fleet
WORKER_REPLICAS=8 docker compose up -d   # ...or scale on the fly`;
const cmdBuild = `# C++ scorer — self-tests score 321 during the build
docker build -t atlas-verifier ./scorer
docker build -t atlas-worker -f backend/AtlasWorker/Dockerfile backend
docker run -d --restart unless-stopped atlas-worker`;
</script>

<template>
  <p class="sub"><router-link to="/network">← {{ t("pages.runVerifier.back") }}</router-link></p>
  <h1>{{ t("pages.runVerifier.title") }}</h1>
  <p class="sub">{{ t("pages.runVerifier.sub") }}</p>

  <div class="banner ok banner--info" v-html="t('pages.runVerifier.oneCommand')"></div>

  <h3>{{ t("pages.runVerifier.dockerH") }}</h3>
  <div class="codeblock">{{ cmdDocker }}</div>
  <p class="muted">{{ t("pages.runVerifier.watchPre") }} <router-link to="/network">{{ t("nav.network") }}</router-link> {{ t("pages.runVerifier.watchPost") }} <span class="mono">docker logs -f atlas-worker</span></p>

  <h3>{{ t("pages.runVerifier.severalH") }}</h3>
  <p class="muted">{{ t("pages.runVerifier.severalNote") }}</p>
  <div class="codeblock">{{ cmdCompose }}</div>
  <div class="codeblock">{{ cmdComposeUp }}</div>

  <h3>{{ t("pages.runVerifier.buildH") }}</h3>
  <p class="muted">{{ t("pages.runVerifier.buildNote") }}</p>
  <div class="codeblock">{{ cmdBuild }}</div>

  <div class="grid2">
    <div class="panel pad">
      <h3 class="panel-h">{{ t("pages.runVerifier.requirements") }}</h3>
      <ul class="muted">
        <li>{{ t("pages.runVerifier.req1") }}</li>
        <li>{{ t("pages.runVerifier.req2") }}</li>
        <li>{{ t("pages.runVerifier.req3") }}</li>
      </ul>
    </div>
    <div class="panel pad">
      <h3 class="panel-h">{{ t("pages.runVerifier.configH") }}</h3>
      <ul class="muted">
        <li><span class="mono">ATLAS_URL</span> — {{ t("pages.runVerifier.cfg1") }}</li>
        <li><span class="mono">WORKER_ID</span> — {{ t("pages.runVerifier.cfg2") }}</li>
        <li><span class="mono">POLL_INTERVAL_MS</span> — {{ t("pages.runVerifier.cfg3") }}</li>
      </ul>
    </div>
  </div>

  <h3>{{ t("pages.runVerifier.identityH") }}</h3>
  <p class="muted" v-html="t('pages.runVerifier.identityBody')"></p>

  <h3>{{ t("pages.runVerifier.safeH") }}</h3>
  <p class="muted" v-html="t('pages.runVerifier.safeBody')"></p>
</template>
