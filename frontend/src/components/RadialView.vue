<script setup>
import { ref, watch, onMounted, onBeforeUnmount, nextTick } from "vue";

// Radial chord layout: neurons placed on a ring grouped by type; synapses drawn as
// curved chords through the centre. Ported verbatim from the React reference.
const COLORS = { input: "#3a9bff", output: "#00e0c6", evolution: "#c084fc" };

const props = defineProps({
  graph: Object,
  selected: { type: Number, default: null },
});
const emit = defineEmits(["select"]);

const ref_ = ref(null);
const hover = ref(null);
let cleanup = null;

function draw() {
  if (cleanup) { cleanup(); cleanup = null; }
  const graph = props.graph;
  if (!graph?.nodes?.length) return;
  const c = ref_.value;
  if (!c) return;
  const selected = props.selected;

  const S = 480, dpr = window.devicePixelRatio || 1;
  c.width = S * dpr; c.height = S * dpr;
  c.style.width = "100%"; c.style.maxWidth = S + "px"; c.style.height = "auto";
  const ctx = c.getContext("2d"); ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  const cx = S / 2, cy = S / 2, R = S / 2 - 30;

  const order = { input: 0, evolution: 1, output: 2 };
  const nodes = [...graph.nodes].sort((a, b) => (order[a.type] - order[b.type]) || a.id - b.id);
  const pos = new Map();
  nodes.forEach((n, i) => {
    const ang = (i / nodes.length) * Math.PI * 2 - Math.PI / 2;
    pos.set(n.id, { x: cx + Math.cos(ang) * R, y: cy + Math.sin(ang) * R, ang, type: n.type, value: n.value, id: n.id });
  });

  ctx.clearRect(0, 0, S, S);
  const links = graph.links; const stride = Math.max(1, Math.floor(links.length / 1100));
  ctx.lineWidth = 1;
  for (let i = 0; i < links.length; i += stride) {
    const a = pos.get(links[i].source), b = pos.get(links[i].target); if (!a || !b) continue;
    ctx.globalAlpha = 0.09; ctx.strokeStyle = links[i].weight > 0 ? "#00e0c6" : "#f87171";
    ctx.beginPath(); ctx.moveTo(a.x, a.y);
    ctx.quadraticCurveTo(cx, cy, b.x, b.y); ctx.stroke();
  }
  ctx.globalAlpha = 1;
  for (const p of pos.values()) {
    ctx.fillStyle = COLORS[p.type] || "#889";
    ctx.beginPath(); ctx.arc(p.x, p.y, p.type === "evolution" ? 3.4 : 2.6, 0, 7); ctx.fill();
  }
  const selP = selected != null ? pos.get(selected) : null;
  if (selP) {
    ctx.lineWidth = 1;
    for (let i = 0; i < links.length; i++) {
      if (links[i].source !== selected && links[i].target !== selected) continue;
      const a = pos.get(links[i].source), b = pos.get(links[i].target); if (!a || !b) continue;
      ctx.globalAlpha = 0.5; ctx.strokeStyle = links[i].weight > 0 ? "#00e0c6" : "#f87171";
      ctx.beginPath(); ctx.moveTo(a.x, a.y); ctx.quadraticCurveTo(cx, cy, b.x, b.y); ctx.stroke();
    }
    ctx.globalAlpha = 1;
    ctx.fillStyle = COLORS[selP.type] || "#889";
    ctx.beginPath(); ctx.arc(selP.x, selP.y, 5, 0, 7); ctx.fill();
    ctx.strokeStyle = "#fff"; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.arc(selP.x, selP.y, 8, 0, 7); ctx.stroke(); ctx.lineWidth = 1;
  }

  const pick = (e, maxD) => {
    const rect = c.getBoundingClientRect(); const sx = S / rect.width;
    const mx = (e.clientX - rect.left) * sx, my = (e.clientY - rect.top) * sx;
    let best = null, bd = maxD;
    for (const p of pos.values()) { const d = (p.x - mx) ** 2 + (p.y - my) ** 2; if (d < bd) { bd = d; best = p; } }
    return { best, rect };
  };
  const onMove = (e) => {
    const { best, rect } = pick(e, 60);
    hover.value = best ? { ...best, mx: e.clientX - rect.left, my: e.clientY - rect.top } : null;
  };
  const onLeave = () => { hover.value = null; };
  const onClick = (e) => {
    const { best } = pick(e, 240);
    emit("select", best ? best.id : null);
  };
  c.addEventListener("mousemove", onMove); c.addEventListener("mouseleave", onLeave);
  c.addEventListener("click", onClick);
  cleanup = () => {
    c.removeEventListener("mousemove", onMove);
    c.removeEventListener("mouseleave", onLeave);
    c.removeEventListener("click", onClick);
  };
}

onMounted(() => nextTick(draw));
watch(() => props.graph, () => nextTick(draw));
watch(() => props.selected, () => nextTick(draw));
onBeforeUnmount(() => { if (cleanup) cleanup(); });
</script>

<template>
  <div v-if="!graph?.nodes?.length" class="muted center pad">No graph for this algorithm yet.</div>
  <div v-else class="canvas-wrap">
    <canvas ref="ref_" class="canvas-el canvas-el--pick" />
    <div v-if="hover" class="tooltip mono" :style="{ left: Math.min(hover.mx + 12, 360) + 'px', top: (hover.my + 12) + 'px' }">
      #{{ hover.id }} · {{ hover.type }} · value {{ hover.value }} · <span class="muted">click to inspect</span>
    </div>
    <div class="legend center">
      <span><span class="sw sw--input" /> input</span>
      <span><span class="sw sw--output" /> output</span>
      <span><span class="sw sw--evolution" /> grown</span>
      <span class="muted">neurons on the ring · synapses arc through the centre</span>
    </div>
  </div>
</template>
