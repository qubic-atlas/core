<script setup>
import { ref, watch } from "vue";
import { useRoute } from "vue-router";
import { api, short, fmt } from "../api.js";
import AnnViews from "../components/AnnViews.vue";
import MutationReplay from "../components/MutationReplay.vue";
import Explainer from "../components/Explainer.vue";

const route = useRoute();
const sol = ref(null);
const recon = ref(null);
const verifying = ref(false);
const err = ref(null);

function verify() {
  const hash = route.params.hash;
  verifying.value = true; err.value = null;
  api.verify(hash)
    .then((r) => (recon.value = r))
    .catch((e) => (err.value = String(e)))
    .finally(() => (verifying.value = false));
}

function loadHash() {
  const hash = route.params.hash;
  sol.value = null; recon.value = null; err.value = null;
  api.solution(hash)
    .then((s) => {
      sol.value = s;
      // Already confirmed by the network? Load its reconstruction immediately (cache hit) —
      // no need to make the user click "Verify locally". Unverified proofs still show the button.
      const st = s.verification?.status;
      if (route.query.auto || st === "confirmed" || st === "verified") verify();
    })
    .catch((e) => (err.value = String(e)));
}

watch(() => route.params.hash, loadHash, { immediate: true });
</script>

<template>
  <div v-if="err && !sol" class="banner no">Could not load proof: {{ err }}</div>
  <div v-else-if="!sol" class="center muted pad"><span class="spin" /> loading proof…</div>
  <template v-else>
    <p class="sub"><router-link to="/proofs">← Proofs</router-link></p>
    <h1 class="detail-title">
      <span class="chip" :class="sol.algorithm === 'HyperIdentity' ? 'hi' : 'add'">{{ sol.algorithm }}</span>
      <span class="mono detail-title__hash">{{ short(route.params.hash, 10) }}</span>
    </h1>

    <!-- verification banner -->
    <div v-if="recon" class="banner" :class="recon.passesThreshold ? 'ok' : 'no'">
      <b class="banner__lead">{{ recon.passesThreshold ? "✓ Verified locally" : "✗ Does not pass threshold" }}</b>
      {{ " — " }}re-scored in your verifier from public inputs:&nbsp;
      <b>score {{ recon.score }}</b> vs threshold {{ recon.threshold }}.
      <template v-if="recon.relayClaimedScore >= 0">
        Relay claimed {{ recon.relayClaimedScore }} → <b :class="recon.scoreMatches ? 'txt-good' : 'txt-bad'">{{ recon.scoreMatches ? "matches" : "MISMATCH" }}</b>.
      </template>
      <span class="muted"> ({{ recon.elapsedMs ? recon.elapsedMs + " ms" : "cached" }}, {{ recon.reconstructorVersion }})</span>
    </div>
    <div v-else class="banner ok banner--info">
      This page trusts <b>nothing</b>. Click <b>Verify locally</b> to re-run the canonical Core scorer on the public
      inputs and reconstruct the neural genome independently.
      <button class="primary banner__btn" @click="verify" :disabled="verifying">
        <template v-if="verifying"><span class="spin" /> reconstructing…</template>
        <template v-else>⛬ Verify locally</template>
      </button>
      <span v-if="verifying && sol.algorithm === 'HyperIdentity'" class="muted"> HyperIdentity replays 1000 ticks × 150 mutations — a few seconds.</span>
    </div>
    <div v-if="err" class="banner no">Verify failed: {{ err }}</div>

    <Explainer :algorithm="sol.algorithm" />

    <div class="grid2">
      <div class="panel pad">
        <h3>Proof</h3>
        <div class="kv mono">
          <div class="k">Epoch</div><div class="v">{{ sol.epoch }} · {{ sol.coreVersion }}</div>
          <div class="k">Computor</div><div class="v"><router-link :to="`/computors/${sol.computorId}`">{{ sol.computorId }}</router-link></div>
          <div class="k">Tick</div><div class="v">{{ fmt(sol.tickNumber) }}</div>
          <div class="k">Score rule</div><div class="v">{{ sol.scoreRule }} (threshold {{ fmt(sol.threshold) }})</div>
          <div class="k">Mining seed</div><div class="v">{{ sol.miningSeed }}</div>
          <div class="k">Nonce</div><div class="v">{{ sol.nonce }}</div>
          <div class="k">Genome id</div><div class="v">{{ sol.annGenomeId }}</div>
        </div>
      </div>
      <div class="panel pad">
        <h3>Reconstructed metrics</h3>
        <div v-if="!recon?.metrics" class="muted">Verify to reconstruct structural metrics.</div>
        <div v-else class="stats">
          <div class="stat"><div class="k">Population</div><div class="v">{{ fmt(recon.metrics.population) }}</div></div>
          <div class="stat"><div class="k">Input neurons</div><div class="v">{{ fmt(recon.metrics.inputNeurons) }}</div></div>
          <div class="stat"><div class="k">Output neurons</div><div class="v">{{ fmt(recon.metrics.outputNeurons) }}</div></div>
          <div class="stat"><div class="k">Evolution neurons</div><div class="v">{{ fmt(recon.metrics.evolutionNeurons) }}</div></div>
          <div class="stat"><div class="k">Non-zero synapses</div><div class="v">{{ fmt(recon.metrics.nonzeroSynapses) }}</div></div>
          <div class="stat"><div class="k">Synapse density</div><div class="v">{{ recon.metrics.synapseDensity != null ? (recon.metrics.synapseDensity * 100).toFixed(2) + "%" : "—" }}</div></div>
          <div class="stat"><div class="k">Accepted mutations</div><div class="v">{{ fmt(recon.metrics.acceptedMutations) }} / {{ fmt(recon.metrics.executedMutations) }}</div></div>
          <div class="stat"><div class="k">Ticks</div><div class="v">{{ fmt(recon.metrics.ticks) }}</div></div>
        </div>
      </div>
    </div>

    <div v-if="recon && recon.graph" class="panel pad mt-16">
      <h3 class="flush">The network this miner trained
        <span class="h3-note"> · {{ fmt(recon.graph.nodes.length) }} neurons · {{ fmt(recon.graph.totalLinks) }} synapses{{ recon.graph.truncatedLinks ? ` (showing ${fmt(recon.graph.renderedLinks)})` : "" }}</span>
      </h3>
      <p class="note">
        Signals flow left→right, from inputs through the neurons the miner grew, to the outputs that produce the answer.
        Teal connections strengthen a signal, red ones inhibit it. This is the actual reconstructed network — every
        weight recomputed from public data.
      </p>
      <AnnViews :graph="recon.graph" />
    </div>

    <div v-if="recon && recon.mutationTrace" class="panel pad mt-16">
      <h3 class="flush">How the miner found it <span class="h3-note"> · every mutation, replayed</span></h3>
      <p class="note">
        Each dot is one attempted mutation. The teal line is the best score so far — it only ratchets upward as the
        miner keeps improvements and discards the rest, until it crosses the dashed threshold.
      </p>
      <MutationReplay :trace="recon.mutationTrace" :threshold="recon.threshold" />
    </div>
  </template>
</template>
