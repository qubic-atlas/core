<script setup>
import { computed } from "vue";
import { useRouter } from "vue-router";
import { short, fmt, fmtTime, timeAgo, explorerTick, explorerTx } from "../api.js";
import { t } from "../i18n.js";
import MiniSpark from "./MiniSpark.vue";

// Reusable proofs table — used on the Proofs list, computor detail, and anywhere proofs show.
// `rows` = array of proof objects, or null while loading.
const props = defineProps({
  rows: { type: Array, default: null },
  err: { type: [String, null], default: null },
  hideComputor: { type: Boolean, default: false },
  emptyText: { type: String, default: "" },
});

const router = useRouter();
const cols = computed(() => (props.hideComputor ? 8 : 9));

function open(hash) { router.push(`/proofs/${hash}`); }

// verification badge → { cls, label } (status word translated)
function badge(v) {
  const s = v?.status;
  if (s === "confirmed" || s === "verified") return { cls: "ok", label: "✓ " + t(s === "verified" ? "table.stVerified" : "table.stConfirmed") };
  if (s === "conflicted") return { cls: "no", label: "⚠ " + t("table.stConflicted") };
  if (s === "failed") return { cls: "no", label: "✗ " + t("table.stFailed") };
  return { cls: "muted-chip", label: t("table.stUnverified") };
}
</script>

<template>
  <div class="panel table-wrap">
    <table>
      <thead>
        <tr>
          <th>{{ t("table.proof") }}</th><th>{{ t("table.algorithm") }}</th><th>{{ t("table.epoch") }}</th>
          <th v-if="!hideComputor">{{ t("table.computor") }}</th>
          <th>{{ t("table.tick") }}</th><th>{{ t("table.when") }}</th><th>{{ t("table.threshold") }}</th><th>{{ t("table.status") }}</th><th>{{ t("table.search") }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="err"><td :colspan="cols" class="center bad">{{ t("table.failedToLoad") }}: {{ err }}</td></tr>
        <tr v-else-if="!rows"><td :colspan="cols" class="center muted"><span class="spin" /> {{ t("common.loading") }}</td></tr>
        <tr v-else-if="rows.length === 0"><td :colspan="cols" class="center muted">{{ emptyText || t("table.noProofs") }}</td></tr>
        <tr v-for="s in rows" v-else :key="s.hash" class="click" @click="open(s.hash)">
          <td class="mono">
            {{ short(s.hash, 7) }}
            <a :href="explorerTx(s.hash)" target="_blank" rel="noopener" class="ext" title="View transaction on the Qubic explorer" @click.stop>↗</a>
          </td>
          <td><span class="chip" :class="s.algorithm === 'HyperIdentity' ? 'hi' : 'add'">{{ s.algorithm }}</span></td>
          <td>{{ s.epoch }}</td>
          <td v-if="!hideComputor" class="mono"><router-link :to="`/computors/${s.computorId}`" @click.stop>{{ short(s.computorId, 6) }}</router-link></td>
          <td class="nowrap">
            <a :href="explorerTick(s.tickNumber)" target="_blank" rel="noopener" class="ext-plain" title="View tick on the Qubic explorer" @click.stop>{{ fmt(s.tickNumber) }} ↗</a>
          </td>
          <td class="nowrap" :title="fmtTime(s.timestamp)">
            <span v-if="s.timestamp">{{ timeAgo(s.timestamp) }}</span>
            <span v-else class="muted">—</span>
          </td>
          <td>{{ fmt(s.threshold) }}</td>
          <td><span class="chip" :class="badge(s.verification).cls">{{ badge(s.verification).label }}</span></td>
          <td>
            <MiniSpark v-if="s.verification?.spark?.length" :spark="s.verification.spark" :threshold="s.threshold" />
            <span v-else class="muted">—</span>
          </td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
