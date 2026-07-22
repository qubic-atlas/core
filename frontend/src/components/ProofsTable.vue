<script setup>
import { computed } from "vue";
import { useRouter } from "vue-router";
import { short, fmt } from "../api.js";
import MiniSpark from "./MiniSpark.vue";

// Reusable proofs table — used on the Proofs list, computor detail, and anywhere proofs show.
// `rows` = array of proof objects, or null while loading.
const props = defineProps({
  rows: { type: Array, default: null },
  err: { type: [String, null], default: null },
  hideComputor: { type: Boolean, default: false },
  emptyText: { type: String, default: "No proofs." },
});

const router = useRouter();
const cols = computed(() => (props.hideComputor ? 7 : 8));

function open(hash) { router.push(`/proofs/${hash}`); }

// verification badge → { cls, label }
function badge(v) {
  const s = v?.status;
  if (s === "confirmed" || s === "verified") return { cls: "ok", label: "✓ " + s };
  if (s === "conflicted") return { cls: "no", label: "⚠ conflicted" };
  if (s === "failed") return { cls: "no", label: "✗ failed" };
  return { cls: "muted-chip", label: "unverified" };
}
</script>

<template>
  <div class="panel table-wrap">
    <table>
      <thead>
        <tr>
          <th>Proof</th><th>Algorithm</th><th>Epoch</th>
          <th v-if="!hideComputor">Computor</th>
          <th>Tick</th><th>Threshold</th><th>Status</th><th>Search</th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="err"><td :colspan="cols" class="center bad">Failed to load: {{ err }}</td></tr>
        <tr v-else-if="!rows"><td :colspan="cols" class="center muted"><span class="spin" /> loading…</td></tr>
        <tr v-else-if="rows.length === 0"><td :colspan="cols" class="center muted">{{ emptyText }}</td></tr>
        <tr v-for="s in rows" v-else :key="s.hash" class="click" @click="open(s.hash)">
          <td class="mono">{{ short(s.hash, 7) }}</td>
          <td><span class="chip" :class="s.algorithm === 'HyperIdentity' ? 'hi' : 'add'">{{ s.algorithm }}</span></td>
          <td>{{ s.epoch }}</td>
          <td v-if="!hideComputor" class="mono"><router-link :to="`/computors/${s.computorId}`" @click.stop>{{ short(s.computorId, 6) }}</router-link></td>
          <td>{{ fmt(s.tickNumber) }}</td>
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
