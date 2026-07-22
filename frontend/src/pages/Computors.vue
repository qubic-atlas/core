<script setup>
import { ref, onMounted } from "vue";
import { useRouter } from "vue-router";
import { api, short, fmt } from "../api.js";

const router = useRouter();
const rows = ref(null);
const err = ref(null);

onMounted(() => {
  api.computors(100, 0).then((d) => (rows.value = d.items || [])).catch((e) => (err.value = String(e)));
});
</script>

<template>
  <h1>Computors</h1>
  <p class="sub">Ranked by proofs our verifier network has confirmed. Identities are recovered from each proof transaction's source — click one for details.</p>
  <div class="panel table-wrap">
    <table>
      <thead><tr><th>#</th><th>Computor</th><th>Proofs</th><th>First tick</th><th>Last tick</th></tr></thead>
      <tbody>
        <tr v-if="err"><td colspan="5" class="center">Failed: {{ err }}</td></tr>
        <tr v-else-if="!rows"><td colspan="5" class="center muted"><span class="spin" /> loading…</td></tr>
        <tr v-for="c in rows" v-else :key="c.computorId" class="click" @click="router.push(`/computors/${c.computorId}`)">
          <td class="muted">{{ c.rank }}</td>
          <td class="mono">{{ short(c.computorId, 12) }}</td>
          <td>{{ fmt(c.solutions) }}</td>
          <td>{{ fmt(c.firstTick) }}</td>
          <td>{{ fmt(c.lastTick) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
