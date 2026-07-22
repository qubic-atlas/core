<script setup>
import { ref, computed, watch, onMounted, onBeforeUnmount, nextTick } from "vue";

// Replays the mutation search: score-over-steps chart + scrubber + per-step detail.
// Drawing logic ported verbatim from the React reference.
const props = defineProps({
  trace: Object,
  threshold: Number,
});

const events = computed(() => props.trace?.events || []);
const step = ref(events.value.length ? events.value.length - 1 : 0);
const playing = ref(false);
const cvs = ref(null);
let playTimer = 0;

watch(playing, (p) => {
  clearInterval(playTimer);
  if (!p) return;
  playTimer = setInterval(() => {
    if (step.value >= events.value.length - 1) { playing.value = false; }
    else step.value += 1;
  }, 60);
});

function drawChart() {
  const evs = events.value;
  if (!evs.length) return;
  const c = cvs.value;
  if (!c) return;
  const W = 720, H = 200, dpr = window.devicePixelRatio || 1;
  c.width = W * dpr; c.height = H * dpr; c.style.width = "100%"; c.style.maxWidth = W + "px"; c.style.height = "auto";
  const ctx = c.getContext("2d"); ctx.setTransform(dpr, 0, 0, dpr, 0, 0); ctx.clearRect(0, 0, W, H);
  const pad = 34;
  const threshold = props.threshold;
  const scores = evs.flatMap((e) => [e.bestScore, e.candidateScore]);
  let lo = Math.min(...scores, threshold), hi = Math.max(...scores, threshold);
  if (hi === lo) hi = lo + 1;
  const X = (i) => pad + (i / Math.max(1, evs.length - 1)) * (W - pad - 10);
  const Y = (v) => H - pad - ((v - lo) / (hi - lo)) * (H - pad - 12);

  ctx.strokeStyle = "#fbbf24"; ctx.setLineDash([5, 4]); ctx.beginPath(); ctx.moveTo(pad, Y(threshold)); ctx.lineTo(W - 10, Y(threshold)); ctx.stroke(); ctx.setLineDash([]);
  ctx.fillStyle = "#fbbf24"; ctx.font = "11px monospace"; ctx.fillText(`threshold ${threshold}`, pad + 4, Y(threshold) - 5);

  for (let i = 0; i < evs.length; i++) {
    const e = evs[i];
    ctx.fillStyle = e.accepted ? "#34d399" : "#4b5b6d";
    ctx.beginPath(); ctx.arc(X(i), Y(e.candidateScore), i <= step.value ? 2.4 : 1.2, 0, 7); ctx.fill();
  }
  ctx.strokeStyle = "#00e0c6"; ctx.lineWidth = 2; ctx.beginPath();
  for (let i = 0; i <= step.value; i++) { const p = X(i), q = Y(evs[i].bestScore); i ? ctx.lineTo(p, q) : ctx.moveTo(p, q); }
  ctx.stroke();
  ctx.strokeStyle = "#dce7f2"; ctx.globalAlpha = .5; ctx.beginPath(); ctx.moveTo(X(step.value), 6); ctx.lineTo(X(step.value), H - pad); ctx.stroke(); ctx.globalAlpha = 1;
  ctx.fillStyle = "#8397ac"; ctx.font = "11px monospace";
  ctx.fillText(String(hi), 4, Y(hi) + 4); ctx.fillText(String(lo), 4, Y(lo));
}

onMounted(() => nextTick(drawChart));
watch([events, step, () => props.threshold], () => nextTick(drawChart));
onBeforeUnmount(() => clearInterval(playTimer));

const e = computed(() => events.value[step.value]);
const crossedAt = computed(() => events.value.findIndex((x) => x.bestScore >= props.threshold));
const narrate = computed(() => {
  const ev = e.value;
  if (!ev) return "";
  const grew = ev.populationAfter > ev.populationBefore;
  return ev.accepted
    ? (ev.candidateScore >= ev.bestScore
        ? `Kept — this mutation ${grew ? "grew a new neuron and " : ""}improved the network to ${ev.bestScore}.`
        : `Kept — a neutral change (score held at ${ev.bestScore}).`)
    : `Discarded — this mutation would have dropped the score to ${ev.candidateScore}, so it was rolled back.`;
});

function togglePlay() {
  if (step.value >= events.value.length - 1) step.value = 0;
  playing.value = !playing.value;
}
function onScrub(ev) { playing.value = false; step.value = +ev.target.value; }
</script>

<template>
  <div v-if="!events.length" class="muted center pad">No mutation trace for this proof.</div>
  <div v-else>
    <canvas ref="cvs" class="canvas-el canvas-el--replay" />
    <div class="controls controls--center">
      <button class="primary" @click="togglePlay">{{ playing ? "❚❚ Pause" : "▶ Play" }}</button>
      <input class="replay-range" type="range" :min="0" :max="events.length - 1" :value="step" @input="onScrub" />
      <span class="mono muted">step {{ e.step + 1 }}/{{ events.length }}</span>
    </div>
    <div class="narrate" :class="e.accepted ? 'narrate--ok' : 'narrate--no'">
      <b :class="e.accepted ? 'txt-good' : 'txt-bad'">Step {{ e.step + 1 }}:</b> {{ narrate }}
      <span v-if="crossedAt >= 0 && step >= crossedAt" class="txt-warn">&nbsp;✓ crossed the threshold at step {{ events[crossedAt].step + 1 }}.</span>
    </div>
    <div class="stats mt-8">
      <div class="stat"><div class="k">Best score</div><div class="v">{{ e.bestScore }}</div></div>
      <div class="stat"><div class="k">This attempt</div><div class="v">{{ e.candidateScore }}</div></div>
      <div class="stat"><div class="k">Outcome</div><div class="v" :class="e.accepted ? 'stat__v--good' : 'stat__v--dim'">{{ e.accepted ? "kept" : "discarded" }}</div></div>
      <div class="stat"><div class="k">Neurons</div><div class="v">{{ e.populationBefore }}{{ e.populationAfter !== e.populationBefore ? ` → ${e.populationAfter}` : "" }}</div></div>
    </div>
  </div>
</template>
