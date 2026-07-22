<script setup>
import { ref, onMounted } from "vue";
import { api, fmt } from "../api.js";
import { t } from "../i18n.js";

const rows = ref(null);
const minEpoch = ref(null);
const err = ref(null);

onMounted(() => {
  api.epochs()
    .then((d) => { rows.value = d.items || []; minEpoch.value = d.minSupportedEpoch ?? null; })
    .catch((e) => (err.value = String(e)));
});

const covPct = (e) => Math.round((e.coverage || 0) * 100);
</script>

<template>
  <h1>{{ t("pages.epochs.title") }}</h1>
  <p class="sub">{{ t("pages.epochs.sub") }}</p>
  <div class="panel table-wrap">
    <table>
      <thead><tr>
        <th>{{ t("pages.epochs.colEpoch") }}</th><th>{{ t("pages.epochs.colCore") }}</th>
        <th>{{ t("pages.epochs.colAlgo") }}</th><th>{{ t("pages.epochs.colFirst") }}</th>
        <th>{{ t("pages.epochs.colLast") }}</th>
        <th>{{ t("pages.epochs.colThreshold") }}</th>
        <th>{{ t("pages.epochs.colVerified") }}</th>
        <th>{{ t("pages.epochs.colCoverage") }}</th>
      </tr></thead>
      <tbody>
        <tr v-if="err"><td colspan="8" class="center">{{ t("common.failed") }}: {{ err }}</td></tr>
        <tr v-else-if="!rows"><td colspan="8" class="center muted"><span class="spin" /> {{ t("common.loading") }}</td></tr>
        <tr v-for="e in rows" v-else :key="e.epoch">
          <td><b>{{ e.epoch }}</b></td>
          <td class="mono">{{ e.coreVersion }}</td>
          <td class="muted">{{ e.algoFamily }}</td>
          <td>{{ fmt(e.firstTick) }}</td>
          <td>{{ fmt(e.lastTick) }}</td>
          <td class="mono nowrap"><span class="chip hi">HI {{ fmt(e.hiThreshold) }}</span> <span class="chip add">ADD {{ fmt(e.addThreshold) }}</span></td>
          <td>
            <span v-if="!e.verified" class="muted">—</span>
            <span v-else class="chip ok">{{ fmt(e.verified) }}<span v-if="e.conflicted"> · {{ e.conflicted }} {{ t("pages.epochs.conflicted") }}</span></span>
          </td>
          <td class="nowrap">
            <div v-if="e.estimatedProofs" class="cov" :title="`${fmt(e.verified)} verified of ~${fmt(e.estimatedProofs)} estimated`">
              <div class="cov__bar"><div class="cov__fill" :style="{ width: covPct(e) + '%' }"></div></div>
              <span class="cov__pct">{{ covPct(e) }}%</span>
            </div>
            <span v-else class="muted" :title="t('pages.epochs.estimating')">—</span>
          </td>
        </tr>
      </tbody>
    </table>
  </div>

  <div class="banner ok banner--info epochs-note">
    <b>{{ t("pages.epochs.scopeLabel") }}</b>
    <span v-html="' ' + t('pages.epochs.scope', { min: minEpoch ?? 197 })"></span>
  </div>
</template>
