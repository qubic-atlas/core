<script setup>
import { ref, computed, onMounted, onBeforeUnmount, watch } from "vue";
import ForceGraph3D from "3d-force-graph";
import { fmt } from "../api.js";
import { t } from "../i18n.js";

// 3D force-directed view of the reconstructed network. Neurons are points in space, synapses are
// colored links (teal = strengthening, red = inhibiting). Synapses are truncated to the strongest
// by default (3D stays smooth); the user can add more or go full screen and orbit/zoom/pan.
const props = defineProps({ graph: { type: Object, default: null } });
const emit = defineEmits(["select"]);

const DEFAULT_LINKS = 1200;
const HARD_CAP = 40000;

const canvas = ref(null);
const wrap = ref(null);
const maxLinks = ref(DEFAULT_LINKS);
const isFull = ref(false);
const playing = ref(false);
let fg = null;

const NODE_COLOR = { input: "#3a9bff", output: "#00e0c6", evolution: "#c084fc" };
const available = computed(() => Math.min(HARD_CAP, props.graph?.links?.length || 0));
const shown = computed(() => Math.min(maxLinks.value, available.value));
// ~200 slider stops regardless of size, so dragging is smooth on huge graphs.
const sliderStep = computed(() => Math.max(1, Math.round(available.value / 200)));

// Strongest |weight| synapses first, capped to maxLinks.
function pickLinks() {
  const links = props.graph?.links || [];
  return [...links]
    .sort((a, b) => Math.abs(b.weight) - Math.abs(a.weight))
    .slice(0, maxLinks.value)
    .map((l) => ({ source: l.source, target: l.target, weight: l.weight }));
}

function feed() {
  if (!fg || !props.graph) return;
  const nodes = (props.graph.nodes || []).map((n) => ({ id: n.id, type: n.type, value: n.value }));
  fg.graphData({ nodes, links: pickLinks() });
}

function sizeToBox() {
  if (!fg || !canvas.value) return;
  fg.width(canvas.value.clientWidth).height(canvas.value.clientHeight || 460);
}

// "Play": animated particles travel along each synapse (source → target), so you watch signals
// flow through the network. Bright teal for strengthening synapses, red for inhibiting.
function applyParticles() {
  if (!fg) return;
  fg.linkDirectionalParticles(playing.value ? 1 : 0)
    .linkDirectionalParticleSpeed(0.006)
    .linkDirectionalParticleWidth(1.6)
    .linkDirectionalParticleColor((l) => (l.weight >= 0 ? "#8ffff0" : "#ffb4b4"));
}
function togglePlay() { playing.value = !playing.value; applyParticles(); }

function toggleFull() {
  if (!document.fullscreenElement) wrap.value?.requestFullscreen?.();
  else document.exitFullscreen?.();
}
function onFsChange() { isFull.value = !!document.fullscreenElement; setTimeout(sizeToBox, 120); }

onMounted(() => {
  fg = ForceGraph3D()(canvas.value)
    .backgroundColor("#0b1016")
    .showNavInfo(false)
    .nodeRelSize(3)
    .nodeVal((n) => (n.type === "evolution" ? 1.6 : 1))
    .nodeColor((n) => NODE_COLOR[n.type] || "#8397ac")
    .nodeOpacity(0.92)
    .nodeLabel((n) => `${n.type} · #${n.id}`)
    .linkColor((l) => (l.weight >= 0 ? "#00e0c6" : "#f87171"))
    .linkOpacity(0.25)
    .linkWidth((l) => Math.min(2, Math.abs(l.weight) * 0.4 + 0.15))
    .onNodeClick((n) => emit("select", n.id));
  sizeToBox();
  feed();
  applyParticles();
  window.addEventListener("resize", sizeToBox);
  document.addEventListener("fullscreenchange", onFsChange);
});

onBeforeUnmount(() => {
  window.removeEventListener("resize", sizeToBox);
  document.removeEventListener("fullscreenchange", onFsChange);
  try { fg && fg._destructor && fg._destructor(); } catch { /* ignore */ }
});

// New graph → reset the cap and rebuild immediately.
watch(() => props.graph, () => { maxLinks.value = Math.min(DEFAULT_LINKS, available.value || DEFAULT_LINKS); feed(); });
// Slider drag → debounce so we don't re-run the force layout on every pixel.
let feedTimer = 0;
watch(maxLinks, () => { clearTimeout(feedTimer); feedTimer = setTimeout(feed, 140); });
</script>

<template>
  <div ref="wrap" class="g3d" :class="{ 'g3d--full': isFull }">
    <div class="g3d__bar">
      <label class="g3d__slider">
        <span class="g3d__count muted">{{ t("pages.proofDetail.synapses") }}: <b>{{ fmt(shown) }}</b> / {{ fmt(available) }}</span>
        <input type="range" min="50" :max="available || 50" :step="sliderStep" v-model.number="maxLinks"
          :disabled="available <= 50" :aria-label="t('pages.proofDetail.synapses')" />
      </label>
      <button class="btn--sm" :class="{ primary: playing }" @click="togglePlay">{{ playing ? t("pages.proofDetail.pause") : t("pages.proofDetail.play") }}</button>
      <button class="btn--sm" @click="toggleFull">{{ isFull ? t("pages.proofDetail.exitFull") : t("pages.proofDetail.fullScreen") }}</button>
    </div>
    <div ref="canvas" class="g3d__canvas"></div>
    <span class="g3d__hint muted">{{ t("pages.proofDetail.orbitHint") }}</span>
  </div>
</template>
