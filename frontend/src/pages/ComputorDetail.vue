<script setup>
import { ref, watch, onMounted } from "vue";
import { useRoute } from "vue-router";
import { api, fmt } from "../api.js";
import ProofsTable from "../components/ProofsTable.vue";

const route = useRoute();
const data = ref(null);
const err = ref(null);

function load() {
  data.value = null; err.value = null;
  api.computor(route.params.id).then((d) => (data.value = d)).catch((e) => (err.value = String(e)));
}
watch(() => route.params.id, load);
onMounted(load);
</script>

<template>
  <p class="sub"><router-link to="/computors">← Computors</router-link></p>
  <h1>Computor</h1>
  <p class="sub mono">{{ route.params.id }}</p>

  <div v-if="err" class="banner no">Failed to load: {{ err }}</div>
  <div v-else-if="!data" class="center muted pad"><span class="spin" /> loading…</div>

  <template v-else>
    <div class="stats stats--live">
      <div class="stat"><div class="k">Proofs (verified)</div><div class="v">{{ fmt(data.proofs) }}</div></div>
      <div class="stat"><div class="k">HyperIdentity</div><div class="v">{{ fmt(data.algorithms?.HyperIdentity) }}</div></div>
      <div class="stat"><div class="k">Addition</div><div class="v">{{ fmt(data.algorithms?.Addition) }}</div></div>
      <div class="stat"><div class="k">Epochs</div><div class="v">{{ data.epochs?.length ? data.epochs.join(", ") : "—" }}</div></div>
      <div class="stat"><div class="k">First tick</div><div class="v">{{ fmt(data.firstTick) }}</div></div>
      <div class="stat"><div class="k">Last tick</div><div class="v">{{ fmt(data.lastTick) }}</div></div>
    </div>

    <p class="muted panel-note">This computor is a Qubic identity — a payable address. Counts are from proofs our verifier network has confirmed{{ data.source === "index" ? " (from the indexed window)" : "" }}.</p>

    <h3>Recent proofs</h3>
    <ProofsTable :rows="data.recent" hide-computor empty-text="No verified proofs yet for this computor." />
  </template>
</template>
