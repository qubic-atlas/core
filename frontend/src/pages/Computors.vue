<script setup>
import { ref, onMounted } from "vue";
import { useRouter } from "vue-router";
import { api, short, fmt } from "../api.js";
import { t } from "../i18n.js";

const router = useRouter();
const rows = ref(null);
const err = ref(null);

onMounted(() => {
  api.computors(100, 0).then((d) => (rows.value = d.items || [])).catch((e) => (err.value = String(e)));
});
</script>

<template>
  <h1>{{ t("pages.computors.title") }}</h1>
  <p class="sub">{{ t("pages.computors.sub") }}</p>
  <div class="panel table-wrap">
    <table>
      <thead><tr><th>#</th><th>{{ t("table.computor") }}</th><th>{{ t("table.proofs") }}</th><th>{{ t("table.firstTick") }}</th><th>{{ t("table.lastTick") }}</th></tr></thead>
      <tbody>
        <tr v-if="err"><td colspan="5" class="center">{{ t("common.failed") }}: {{ err }}</td></tr>
        <tr v-else-if="!rows"><td colspan="5" class="center muted"><span class="spin" /> {{ t("common.loading") }}</td></tr>
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
