<script setup>
import { ref, watch } from "vue";
import { useRoute } from "vue-router";
import { api, short, fmt, fmtTime, timeAgo, explorerTick, explorerTx } from "../api.js";
import { t } from "../i18n.js";
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
  <div v-if="err && !sol" class="banner no">{{ t("pages.proofDetail.couldNotLoad") }}: {{ err }}</div>
  <div v-else-if="!sol" class="center muted pad"><span class="spin" /> {{ t("pages.proofDetail.loadingProof") }}</div>
  <template v-else>
    <p class="sub"><router-link to="/proofs">← {{ t("nav.proofs") }}</router-link></p>
    <h1 class="detail-title">
      <span class="chip" :class="sol.algorithm === 'HyperIdentity' ? 'hi' : 'add'">{{ sol.algorithm }}</span>
      <span class="mono detail-title__hash">{{ short(route.params.hash, 10) }}</span>
      <a :href="explorerTx(route.params.hash)" target="_blank" rel="noopener" class="ext" title="View transaction on the Qubic explorer">↗</a>
    </h1>

    <!-- verification banner -->
    <div v-if="recon" class="banner" :class="recon.passesThreshold ? 'ok' : 'no'">
      <b class="banner__lead">{{ recon.passesThreshold ? t("pages.proofDetail.verifiedLocally") : t("pages.proofDetail.notPass") }}</b>
      {{ " — " }}{{ t("pages.proofDetail.rescored") }}&nbsp;
      <b>{{ t("pages.proofDetail.scoreWord") }} {{ recon.score }}</b> {{ t("pages.proofDetail.vsThreshold") }} {{ recon.threshold }}.
      <template v-if="recon.relayClaimedScore >= 0">
        {{ t("pages.proofDetail.relayClaimed") }} {{ recon.relayClaimedScore }} → <b :class="recon.scoreMatches ? 'txt-good' : 'txt-bad'">{{ recon.scoreMatches ? t("pages.proofDetail.matches") : t("pages.proofDetail.mismatch") }}</b>.
      </template>
      <span class="muted"> ({{ recon.elapsedMs ? recon.elapsedMs + " ms" : t("pages.proofDetail.cached") }}, {{ recon.reconstructorVersion }})</span>
    </div>
    <div v-else class="banner ok banner--info">
      <span v-html="t('pages.proofDetail.trustNothing')"></span>
      <button class="primary banner__btn" @click="verify" :disabled="verifying">
        <template v-if="verifying"><span class="spin" /> {{ t("pages.proofDetail.reconstructing") }}</template>
        <template v-else>{{ t("pages.proofDetail.verifyLocally") }}</template>
      </button>
      <span v-if="verifying && sol.algorithm === 'HyperIdentity'" class="muted"> {{ t("pages.proofDetail.hiHint") }}</span>
    </div>
    <div v-if="err" class="banner no">{{ t("pages.proofDetail.verifyFailed") }}: {{ err }}</div>

    <Explainer :algorithm="sol.algorithm" />

    <div class="grid2">
      <div class="panel pad">
        <h3>{{ t("pages.proofDetail.proof") }}</h3>
        <div class="kv mono">
          <div class="k">{{ t("table.epoch") }}</div><div class="v">{{ sol.epoch }} · {{ sol.coreVersion }}</div>
          <div class="k">{{ t("table.computor") }}</div><div class="v"><router-link :to="`/computors/${sol.computorId}`">{{ sol.computorId }}</router-link></div>
          <div class="k">{{ t("table.tick") }}</div><div class="v"><a :href="explorerTick(sol.tickNumber)" target="_blank" rel="noopener" class="ext-plain">{{ fmt(sol.tickNumber) }} ↗</a></div>
          <div class="k">{{ t("table.when") }}</div><div class="v">{{ sol.timestamp ? `${fmtTime(sol.timestamp)} (${timeAgo(sol.timestamp)})` : "—" }}</div>
          <div class="k">{{ t("pages.proofDetail.scoreRule") }}</div><div class="v">{{ sol.scoreRule }} ({{ t("pages.proofDetail.thresholdWord") }} {{ fmt(sol.threshold) }})</div>
          <div class="k">{{ t("pages.proofDetail.miningSeed") }}</div><div class="v">{{ sol.miningSeed }}</div>
          <div class="k">{{ t("pages.proofDetail.nonce") }}</div><div class="v">{{ sol.nonce }}</div>
          <div class="k">{{ t("pages.proofDetail.genomeId") }}</div><div class="v">{{ sol.annGenomeId }}</div>
        </div>
      </div>
      <div class="panel pad">
        <h3>{{ t("pages.proofDetail.reconstructedMetrics") }}</h3>
        <div v-if="!recon?.metrics" class="muted">{{ t("pages.proofDetail.verifyToReconstruct") }}</div>
        <div v-else class="stats">
          <div class="stat"><div class="k">{{ t("pages.proofDetail.population") }}</div><div class="v">{{ fmt(recon.metrics.population) }}</div></div>
          <div class="stat"><div class="k">{{ t("pages.proofDetail.inputNeurons") }}</div><div class="v">{{ fmt(recon.metrics.inputNeurons) }}</div></div>
          <div class="stat"><div class="k">{{ t("pages.proofDetail.outputNeurons") }}</div><div class="v">{{ fmt(recon.metrics.outputNeurons) }}</div></div>
          <div class="stat"><div class="k">{{ t("pages.proofDetail.evolutionNeurons") }}</div><div class="v">{{ fmt(recon.metrics.evolutionNeurons) }}</div></div>
          <div class="stat"><div class="k">{{ t("pages.proofDetail.nonzeroSynapses") }}</div><div class="v">{{ fmt(recon.metrics.nonzeroSynapses) }}</div></div>
          <div class="stat"><div class="k">{{ t("pages.proofDetail.synapseDensity") }}</div><div class="v">{{ recon.metrics.synapseDensity != null ? (recon.metrics.synapseDensity * 100).toFixed(2) + "%" : "—" }}</div></div>
          <div class="stat"><div class="k">{{ t("pages.proofDetail.acceptedMutations") }}</div><div class="v">{{ fmt(recon.metrics.acceptedMutations) }} / {{ fmt(recon.metrics.executedMutations) }}</div></div>
          <div class="stat"><div class="k">{{ t("pages.proofDetail.ticksLabel") }}</div><div class="v">{{ fmt(recon.metrics.ticks) }}</div></div>
        </div>
      </div>
    </div>

    <div v-if="recon && recon.graph" class="panel pad mt-16">
      <h3 class="flush">{{ t("pages.proofDetail.networkTitle") }}
        <span class="h3-note"> · {{ fmt(recon.graph.nodes.length) }} {{ t("pages.proofDetail.neurons") }} · {{ fmt(recon.graph.totalLinks) }} {{ t("pages.proofDetail.synapses") }}{{ recon.graph.truncatedLinks ? ` (${t("pages.proofDetail.showingWord")} ${fmt(recon.graph.renderedLinks)})` : "" }}</span>
      </h3>
      <p class="note">{{ t("pages.proofDetail.networkNote") }}</p>
      <AnnViews :graph="recon.graph" />
    </div>

    <div v-if="recon && recon.mutationTrace" class="panel pad mt-16">
      <h3 class="flush">{{ t("pages.proofDetail.howFound") }} <span class="h3-note"> · {{ t("pages.proofDetail.everyMutation") }}</span></h3>
      <p class="note">{{ t("pages.proofDetail.howFoundNote") }}</p>
      <MutationReplay :trace="recon.mutationTrace" :threshold="recon.threshold" />
    </div>
  </template>
</template>
