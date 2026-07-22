<script setup>
import { ref, onMounted, onBeforeUnmount } from "vue";
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

const acc = (v) => (v.verifications ? Math.round((v.correct / v.verifications) * 100) : 0);

onMounted(() => { load(); timer = setInterval(load, 4000); });
onBeforeUnmount(() => { live = false; clearInterval(timer); });

const statusChip = (s) => (s === "confirmed" ? "ok" : s === "conflicted" ? "no" : s === "resolving" ? "pending" : "");
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
    <b>How it stays honest:</b> the scorer is <b>deterministic</b>, so each Proof has exactly one correct genome. Workers must
    agree on the <b>genome hash</b> to confirm it; a disagreement is settled by a <b>referee re-compute</b> (authoritative,
    because anyone can re-derive the answer), and dissenting workers lose reputation until they're excluded.
  </div>

  <!-- headline stats -->
  <div class="stats stats--live">
    <div class="stat"><div class="k">Proofs verified</div><div class="v">{{ vstats ? fmt(vstats.verified) : "—" }}</div></div>
    <div class="stat"><div class="k">Confirmed</div><div class="v good">{{ vstats ? fmt(vstats.confirmed) : "—" }}</div></div>
    <div class="stat"><div class="k">Conflicted</div><div class="v" :class="{ bad: vstats && vstats.conflicted }">{{ vstats ? fmt(vstats.conflicted) : "—" }}</div></div>
    <div class="stat"><div class="k">Computors seen</div><div class="v">{{ vstats ? fmt(vstats.computors) : "—" }}</div></div>
    <div class="stat"><div class="k">Workers online</div><div class="v accent-text">{{ jstats ? jstats.workersOnline : "—" }}</div></div>
    <div class="stat"><div class="k">Confirmations required</div><div class="v">{{ jstats ? jstats.requiredConfirmations : "—" }}</div></div>
    <div class="stat"><div class="k">Jobs done</div><div class="v">{{ jstats ? fmt(jstats.done) : "—" }}</div></div>
    <div class="stat"><div class="k">Queue</div><div class="v">{{ jstats ? (jstats.pending + jstats.leased + (jstats.resolving||0)) : "—" }}</div></div>
  </div>

  <div class="net-stack">
    <!-- top contributors (donation-ready: each id is a Qubic address) -->
    <div class="panel table-wrap">
      <h3 class="panel-h">Top contributors <span class="muted">· verifiers ranked by confirmed Proofs</span></h3>
      <table>
        <thead><tr><th>#</th><th>Verifier identity</th><th>Verified</th><th>Correct</th><th>Accuracy</th><th>Last active</th></tr></thead>
        <tbody>
          <tr v-if="boardPending"><td colspan="6" class="center muted">Leaderboard warming up — it appears once workers submit signed results.</td></tr>
          <tr v-else-if="!board"><td colspan="6" class="center muted"><span class="spin" /> loading…</td></tr>
          <tr v-else-if="!board.length"><td colspan="6" class="center muted">No verifiers yet — <router-link to="/run-verifier">run one</router-link>.</td></tr>
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
      <p class="muted panel-note">Each verifier is a Qubic identity — a payable address — so heavy contributors can be recognized and rewarded directly.</p>
    </div>

    <!-- workers -->
    <div class="panel table-wrap">
      <h3 class="panel-h">Workers</h3>
      <table>
        <thead><tr><th>Worker</th><th>Done</th><th>Agreed</th><th>Dissent</th><th>Rep.</th><th>Status</th></tr></thead>
        <tbody>
          <tr v-if="!jstats"><td colspan="6" class="center muted"><span class="spin" /> loading…</td></tr>
          <tr v-else-if="!jstats.workers.length"><td colspan="6" class="center muted">No workers have checked in.</td></tr>
          <tr v-for="w in jstats?.workers" :key="w.id">
            <td class="mono">{{ short(w.id, 6) }}</td>
            <td>{{ fmt(w.completed) }}</td>
            <td class="good">{{ w.agreed }}</td>
            <td :class="w.disagreed ? 'bad' : 'muted'">{{ w.disagreed }}</td>
            <td :class="w.reputation < 0 ? 'bad' : 'muted'">{{ w.reputation }}</td>
            <td>
              <span v-if="!w.trusted" class="chip no">excluded</span>
              <span v-else-if="w.online" class="chip ok">online</span>
              <span v-else class="chip pending">idle</span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- recent verifications -->
    <div class="panel table-wrap">
      <h3 class="panel-h">Recent verifications</h3>
      <table>
        <thead><tr><th>Proof</th><th>Genome</th><th>Score</th><th>Conf.</th><th>Status</th></tr></thead>
        <tbody>
          <tr v-if="!recent"><td colspan="5" class="center muted"><span class="spin" /> loading…</td></tr>
          <tr v-else-if="!recent.length"><td colspan="5" class="center muted">No jobs yet.</td></tr>
          <tr v-for="j in recent" :key="j.id">
            <td class="mono"><router-link :to="`/proofs/${j.hash}?auto=1`">{{ short(j.hash, 8) }}</router-link></td>
            <td class="mono muted">{{ j.genomeId ? short(j.genomeId, 6) : "—" }}</td>
            <td>{{ j.verifiedScore != null ? fmt(j.verifiedScore) : "—" }}</td>
            <td>{{ j.confirmations }}</td>
            <td>
              <span class="chip" :class="statusChip(j.status)">{{ j.status }}</span>
              <span v-if="j.resolvedByReferee" class="chip pending" title="settled by referee re-compute">referee</span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </div>

  <p v-if="err" class="banner no">Failed to load network data: {{ err }}</p>
</template>
