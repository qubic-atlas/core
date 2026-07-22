<script setup>
import { ref, watch, onMounted, computed } from "vue";
import { useRoute } from "vue-router";
import { api } from "../api.js";
import { t } from "../i18n.js";
import ProofsTable from "../components/ProofsTable.vue";

const route = useRoute();
const rows = ref(null);
const epochs = ref([]);
const algo = ref("");
const epoch = ref("");
const verifiedOnly = ref(route.query.verified === "1" || route.query.verified === "true");
const offset = ref(0);
const hasMore = ref(false);
const err = ref(null);
const source = ref("");
const limit = 25;

// Context-aware empty message so a blank table explains itself.
const emptyText = computed(() => {
  if (source.value === "verifications-unavailable") return t("pages.proofs.emptyUnavailable");
  if (verifiedOnly.value) return t("pages.proofs.emptyVerified");
  return t("pages.proofs.emptyNone");
});

function load() {
  rows.value = null; err.value = null;
  const p = { limit, offset: offset.value };
  if (algo.value) p.algorithm = algo.value;
  if (epoch.value) p.epoch = epoch.value;
  if (verifiedOnly.value) p.verified = 1;
  api.solutions(p)
    .then((d) => { rows.value = d.items || []; hasMore.value = !!d.hasMore; source.value = d.source || ""; })
    .catch((e) => (err.value = String(e)));
}

function onAlgo(e) { offset.value = 0; algo.value = e.target.value; }
function onEpoch(e) { offset.value = 0; epoch.value = e.target.value; }
function onVerified(e) { offset.value = 0; verifiedOnly.value = e.target.checked; }

watch([algo, epoch, verifiedOnly, offset], load);
onMounted(() => {
  load();
  api.epochs().then((d) => { epochs.value = (d.items || []).map((e) => e.epoch); }).catch(() => {});
});
</script>

<template>
  <h1>{{ t("pages.proofs.title") }}</h1>
  <p class="sub">{{ t("pages.proofs.sub") }}</p>

  <div class="controls">
    <select :value="algo" @change="onAlgo">
      <option value="">{{ t("pages.proofs.allAlgorithms") }}</option>
      <option>HyperIdentity</option>
      <option>Addition</option>
    </select>
    <select :value="epoch" @change="onEpoch">
      <option value="">{{ t("pages.proofs.allEpochs") }}</option>
      <option v-for="e in epochs" :key="e" :value="e">{{ t("pages.proofs.epoch") }} {{ e }}</option>
    </select>
    <label class="switch">
      <input type="checkbox" class="switch__input" :checked="verifiedOnly" @change="onVerified" />
      <span class="switch__track"><span class="switch__knob" /></span>
      <span class="switch__label">{{ t("pages.proofs.verifiedOnly") }}</span>
    </label>
  </div>

  <ProofsTable :rows="rows" :err="err" :empty-text="emptyText" />

  <div class="pagerow">
    <button :disabled="offset === 0" @click="offset = Math.max(0, offset - limit)">{{ t("common.newer") }}</button>
    <span class="muted">{{ t("common.showing") }} {{ offset + 1 }}–{{ offset + (rows?.length || 0) }}</span>
    <button :disabled="!hasMore" @click="offset += limit">{{ t("common.older") }}</button>
  </div>
</template>
