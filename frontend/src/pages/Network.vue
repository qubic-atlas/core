<script setup>
import { ref, computed, watch, onMounted, onBeforeUnmount } from "vue";
import { api, short, fmt } from "../api.js";
import { t } from "../i18n.js";

const vstats = ref(null);   // /verifications/stats
const jstats = ref(null);   // /jobs/stats
const recent = ref(null);   // /jobs/recent
const err = ref(null);
let timer = 0, live = true;

const board = ref(null);   // /verifiers/leaderboard (may 404 until backend lands)
const boardPending = ref(false);

const load = () => Promise.all([
  api.verificationsStats().then((d) => { if (live) vstats.value = d; }).catch(() => {}),
  api.jobsStats().then((d) => { if (live) jstats.value = d; }).catch(() => {}),
  api.jobsRecent().then((d) => { if (live) recent.value = Array.isArray(d) ? d : (d.items || []); }).catch((e) => (err.value = String(e))),
  api.leaderboard(25).then((d) => { if (live) { board.value = Array.isArray(d) ? d : (d.items || []); boardPending.value = false; } })
    .catch(() => { if (live) boardPending.value = true; }),
]);

// Accuracy = agreement among proofs that actually reached consensus (decided), NOT among all
// submissions — a proof still pending consensus is neither right nor wrong for the worker.
const acc = (v) => (v.decided ? Math.round((v.correct / v.decided) * 100) : (v.verifications ? 100 : 0));

onMounted(() => { load(); timer = setInterval(load, 4000); });
onBeforeUnmount(() => { live = false; clearInterval(timer); });

const statusChip = (s) => (s === "confirmed" ? "ok" : s === "conflicted" ? "no" : s === "resolving" ? "pending" : "");

// Worker list paging (client-side) — stays sane when hundreds of workers connect.
const W_PER = 20;
const wPage = ref(0);
const workerCount = computed(() => jstats.value?.workers?.length || 0);
const wPages = computed(() => Math.max(1, Math.ceil(workerCount.value / W_PER)));
const pagedWorkers = computed(() => (jstats.value?.workers || []).slice(wPage.value * W_PER, wPage.value * W_PER + W_PER));
// Clamp the page if the worker count shrinks (workers dropping off).
watch(wPages, (n) => { if (wPage.value >= n) wPage.value = Math.max(0, n - 1); });
</script>

<template>
  <div class="net-head">
    <div>
      <h1>{{ t("pages.network.title") }}</h1>
      <p class="sub">{{ t("pages.network.sub") }}</p>
    </div>
    <router-link to="/run-verifier" class="cta">{{ t("pages.network.runVerifier") }}</router-link>
  </div>

  <div class="banner ok banner--info">
    <b>{{ t("pages.network.honestLabel") }}</b> <span v-html="t('pages.network.honestBody')"></span>
  </div>

  <!-- headline stats -->
  <div class="stats stats--live">
    <div class="stat"><div class="k">{{ t("pages.network.stVerified") }}</div><div class="v">{{ vstats ? fmt(vstats.verified) : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.network.stConfirmed") }}</div><div class="v good">{{ vstats ? fmt(vstats.confirmed) : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.network.stConflicted") }}</div><div class="v" :class="{ bad: vstats && vstats.conflicted }">{{ vstats ? fmt(vstats.conflicted) : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.network.stComputors") }}</div><div class="v">{{ vstats ? fmt(vstats.computors) : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.network.stWorkers") }}</div><div class="v accent-text">{{ jstats ? jstats.workersOnline : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.network.stConfReq") }}</div><div class="v">{{ jstats ? jstats.requiredConfirmations : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.network.stJobs") }}</div><div class="v">{{ jstats ? fmt(jstats.done) : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.network.stQueue") }}</div><div class="v">{{ jstats ? (jstats.pending + jstats.leased + (jstats.resolving||0)) : "—" }}</div></div>
  </div>

  <div class="net-stack">
    <!-- top contributors (donation-ready: each id is a Qubic address) -->
    <div class="panel table-wrap">
      <h3 class="panel-h">{{ t("pages.network.topContrib") }} <span class="muted">· {{ t("pages.network.topContribNote") }}</span></h3>
      <table>
        <thead><tr><th>#</th><th>{{ t("pages.network.lbId") }}</th><th>{{ t("pages.network.lbVerified") }}</th><th>{{ t("pages.network.lbCorrect") }}</th><th>{{ t("pages.network.lbAccuracy") }}</th><th>{{ t("pages.network.lbLastActive") }}</th></tr></thead>
        <tbody>
          <tr v-if="boardPending"><td colspan="6" class="center muted">{{ t("pages.network.lbWarming") }}</td></tr>
          <tr v-else-if="!board"><td colspan="6" class="center muted"><span class="spin" /> {{ t("common.loading") }}</td></tr>
          <tr v-else-if="!board.length"><td colspan="6" class="center muted">{{ t("pages.network.lbNoneA") }}<router-link to="/run-verifier">{{ t("pages.network.lbRunOne") }}</router-link>.</td></tr>
          <tr v-for="v in board" :key="v.id">
            <td class="muted">{{ v.rank }}</td>
            <td class="mono">{{ short(v.id, 12) }}</td>
            <td>{{ fmt(v.verifications) }}</td>
            <td class="good">{{ fmt(v.correct) }}</td>
            <td>{{ acc(v) }}%</td>
            <td class="muted">{{ v.lastSeen ? new Date(v.lastSeen).toLocaleDateString() : "—" }}</td>
          </tr>
        </tbody>
      </table>
      <p class="muted panel-note">{{ t("pages.network.lbNote") }}</p>
    </div>

    <!-- workers -->
    <div class="panel table-wrap">
      <h3 class="panel-h">{{ t("pages.network.workersTitle") }} <span class="muted">· {{ fmt(workerCount) }} {{ t("pages.network.active") }}</span></h3>
      <table>
        <thead><tr><th>{{ t("pages.network.wWorker") }}</th><th>{{ t("pages.network.wDone") }}</th><th>{{ t("pages.network.wAgreed") }}</th><th>{{ t("pages.network.wDissent") }}</th><th>{{ t("pages.network.wRep") }}</th><th>{{ t("pages.network.wStatus") }}</th></tr></thead>
        <tbody>
          <tr v-if="!jstats"><td colspan="6" class="center muted"><span class="spin" /> {{ t("common.loading") }}</td></tr>
          <tr v-else-if="!jstats.workers.length"><td colspan="6" class="center muted">{{ t("pages.network.wNone") }}</td></tr>
          <tr v-for="w in pagedWorkers" :key="w.id">
            <td class="mono">{{ short(w.id, 6) }}</td>
            <td>{{ fmt(w.completed) }}</td>
            <td class="good">{{ w.agreed }}</td>
            <td :class="w.disagreed ? 'bad' : 'muted'">{{ w.disagreed }}</td>
            <td :class="w.reputation < 0 ? 'bad' : 'muted'">{{ w.reputation }}</td>
            <td>
              <span v-if="!w.trusted" class="chip no">{{ t("pages.network.wExcluded") }}</span>
              <span v-else-if="w.online" class="chip ok">{{ t("pages.network.wOnline") }}</span>
              <span v-else class="chip pending">{{ t("pages.network.wIdle") }}</span>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-if="wPages > 1" class="pagerow pagerow--compact">
        <button :disabled="wPage === 0" @click="wPage--">←</button>
        <span class="muted">{{ wPage + 1 }} / {{ wPages }}</span>
        <button :disabled="wPage >= wPages - 1" @click="wPage++">→</button>
      </div>
    </div>

    <!-- recent verifications -->
    <div class="panel table-wrap">
      <h3 class="panel-h">{{ t("pages.network.recentTitle") }}</h3>
      <table>
        <thead><tr><th>{{ t("table.proof") }}</th><th>{{ t("pages.network.rGenome") }}</th><th>{{ t("pages.network.rScore") }}</th><th>{{ t("pages.network.rConf") }}</th><th>{{ t("table.status") }}</th></tr></thead>
        <tbody>
          <tr v-if="!recent"><td colspan="5" class="center muted"><span class="spin" /> {{ t("common.loading") }}</td></tr>
          <tr v-else-if="!recent.length"><td colspan="5" class="center muted">{{ t("pages.network.rNone") }}</td></tr>
          <tr v-for="j in recent" :key="j.id">
            <td class="mono"><router-link :to="`/proofs/${j.hash}?auto=1`">{{ short(j.hash, 8) }}</router-link></td>
            <td class="mono muted">{{ j.genomeId ? short(j.genomeId, 6) : "—" }}</td>
            <td>{{ j.verifiedScore != null ? fmt(j.verifiedScore) : "—" }}</td>
            <td>{{ j.confirmations }}</td>
            <td>
              <span class="chip" :class="statusChip(j.status)">{{ j.status }}</span>
              <span v-if="j.resolvedByReferee" class="chip pending">{{ t("pages.network.rReferee") }}</span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>

  <p v-if="err" class="banner no">{{ t("pages.network.failedNet") }}: {{ err }}</p>
</template>
