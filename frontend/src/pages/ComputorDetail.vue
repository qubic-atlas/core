<script setup>
import { ref, watch, onMounted } from "vue";
import { useRoute } from "vue-router";
import { api, fmt, explorerAddr } from "../api.js";
import { t } from "../i18n.js";
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
  <p class="sub"><router-link to="/computors">← {{ t("nav.computors") }}</router-link></p>
  <h1>{{ t("pages.computorDetail.title") }}</h1>
  <p class="sub mono">
    {{ route.params.id }}
    <a :href="explorerAddr(route.params.id)" target="_blank" rel="noopener" class="ext" title="View address on the Qubic explorer">↗</a>
  </p>

  <div v-if="err" class="banner no">{{ t("table.failedToLoad") }}: {{ err }}</div>
  <div v-else-if="!data" class="center muted pad"><span class="spin" /> {{ t("common.loading") }}</div>

  <template v-else>
    <div class="stats stats--live">
      <div class="stat"><div class="k">{{ t("pages.computorDetail.proofsVerified") }}</div><div class="v">{{ fmt(data.proofs) }}</div></div>
      <div class="stat"><div class="k">{{ t("pages.computorDetail.hyperIdentity") }}</div><div class="v">{{ fmt(data.algorithms?.HyperIdentity) }}</div></div>
      <div class="stat"><div class="k">{{ t("pages.computorDetail.addition") }}</div><div class="v">{{ fmt(data.algorithms?.Addition) }}</div></div>
      <div class="stat"><div class="k">{{ t("pages.computorDetail.epochs") }}</div><div class="v">{{ data.epochs?.length ? data.epochs.join(", ") : "—" }}</div></div>
      <div class="stat"><div class="k">{{ t("pages.computorDetail.firstTick") }}</div><div class="v">{{ fmt(data.firstTick) }}</div></div>
      <div class="stat"><div class="k">{{ t("pages.computorDetail.lastTick") }}</div><div class="v">{{ fmt(data.lastTick) }}</div></div>
    </div>

    <p class="muted panel-note">{{ t("pages.computorDetail.note") }}{{ data.source === "index" ? t("pages.computorDetail.noteIndexed") : "" }}.</p>

    <h3>{{ t("pages.computorDetail.recentProofs") }}</h3>
    <ProofsTable :rows="data.recent" hide-computor :empty-text="t('pages.computorDetail.empty')" />
  </template>
</template>
