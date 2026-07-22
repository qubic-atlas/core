<script setup>
import { ref, watch, onMounted, onBeforeUnmount, nextTick } from "vue";

// Animated, layered view of the reconstructed ANN. Inputs (left) → outputs (right),
// evolution neurons (center). Signal pulses flow along synapses so a viewer can *see*
// the network computing. Edge/pulse color = synapse sign.
// Drawing / hit-testing logic ported verbatim from the React reference.
const COLORS = { input: "#3a9bff", output: "#00e0c6", evolution: "#c084fc" };

const props = defineProps({
  graph: Object,
  selected: { type: Number, default: null },
});
const emit = defineEmits(["select"]);

const canvasRef = ref(null);
const hover = ref(null);
const flow = ref(true);

// mutable refs kept outside reactivity
let rafId = 0;
let selectedRef = props.selected;
let cleanupListeners = null;
watch(() => props.selected, (v) => { selectedRef = v; });

function setup() {
  teardown();
  const graph = props.graph;
  if (!graph || !graph.nodes.length) return;
  const canvas = canvasRef.value;
  if (!canvas) return;

  const W = 760, H = 480, dpr = window.devicePixelRatio || 1;
  canvas.width = W * dpr; canvas.height = H * dpr;
  canvas.style.width = "100%"; canvas.style.maxWidth = W + "px"; canvas.style.height = "auto";
  const ctx = canvas.getContext("2d"); ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

  const cols = { input: [], output: [], evolution: [] };
  graph.nodes.forEach((n) => (cols[n.type] || cols.evolution).push(n));
  const hasEvo = cols.evolution.length > 0;
  const pos = new Map();
  const top = 46, bot = H - 34;

  const setPos = (n, x, y, type) => pos.set(n.id, { x, y, type, value: n.value, id: n.id });
  const place = (arr, cx, maxW, type) => {
    const N = arr.length;
    if (!N) return;
    if (N === 1) { setPos(arr[0], cx, (top + bot) / 2, type); return; }
    const single = N <= 22;
    const cols2 = single ? 1
      : Math.min(Math.max(1, Math.round(Math.sqrt((N * maxW) / (bot - top)))), Math.max(1, Math.floor(maxW / 9)));
    const rows = Math.ceil(N / cols2);
    arr.forEach((n, i) => {
      const col = i % cols2, row = Math.floor(i / cols2);
      const x = cols2 === 1 ? cx : cx - maxW / 2 + (col * maxW) / (cols2 - 1);
      const y = rows === 1 ? (top + bot) / 2 : top + (row * (bot - top)) / (rows - 1);
      const j = ((n.id * 2654435761) % 7) - 3;
      setPos(n, x + j * 0.5, y + (((n.id * 40503) % 7) - 3) * 0.5, type);
    });
  };

  if (hasEvo) {
    place(cols.input, 96, 120, "input");
    place(cols.evolution, W / 2, 320, "evolution");
    place(cols.output, W - 96, 120, "output");
  } else {
    place(cols.input, 150, 210, "input");
    place(cols.output, W - 150, 210, "output");
  }
  const centerX = { input: hasEvo ? 96 : 150, output: hasEvo ? W - 96 : W - 150, evolution: W / 2 };

  const links = [];
  for (const l of graph.links) {
    const a = pos.get(l.source), b = pos.get(l.target);
    if (a && b) links.push({ a, b, w: l.weight });
    if (links.length >= 1400) break;
  }
  const pulseEdges = links.filter((l) => l.a.x < l.b.x);
  const usePulseEdges = pulseEdges.length ? pulseEdges : links;
  const PULSES = Math.min(140, usePulseEdges.length);
  const mkPulse = (i) => {
    const e = usePulseEdges[(i * 2654435761 >>> 0) % usePulseEdges.length];
    return { e, t: ((i * 0.618) % 1), speed: 0.006 + ((i * 97) % 100) / 100 * 0.012 };
  };
  const pulses = Array.from({ length: PULSES }, (_, i) => mkPulse(i));

  const rNode = (type) => (type === "evolution" ? (cols.evolution.length > 120 ? 3.4 : 4.6) : (cols.input.length + cols.output.length > 200 ? 2.6 : 3.2));

  const drawStatic = () => {
    ctx.clearRect(0, 0, W, H);
    ctx.lineWidth = 1;
    for (const l of links) {
      ctx.globalAlpha = 0.07;
      ctx.strokeStyle = l.w > 0 ? "#00e0c6" : "#f87171";
      const mx = (l.a.x + l.b.x) / 2, my = (l.a.y + l.b.y) / 2 - 20;
      ctx.beginPath(); ctx.moveTo(l.a.x, l.a.y); ctx.quadraticCurveTo(mx, my, l.b.x, l.b.y); ctx.stroke();
    }
    ctx.globalAlpha = 1;
  };
  const drawNodes = () => {
    const sel = selectedRef;
    let selP = null;
    for (const p of pos.values()) {
      if (p.id === sel) { selP = p; continue; }
      ctx.fillStyle = COLORS[p.type] || "#889";
      ctx.beginPath(); ctx.arc(p.x, p.y, rNode(p.type), 0, 7); ctx.fill();
    }
    if (selP) {
      ctx.fillStyle = COLORS[selP.type] || "#889";
      ctx.beginPath(); ctx.arc(selP.x, selP.y, rNode(selP.type) + 1.5, 0, 7); ctx.fill();
      ctx.strokeStyle = "#fff"; ctx.lineWidth = 2; ctx.globalAlpha = 0.95;
      ctx.beginPath(); ctx.arc(selP.x, selP.y, rNode(selP.type) + 5, 0, 7); ctx.stroke();
      ctx.globalAlpha = 1; ctx.lineWidth = 1;
    }
    ctx.fillStyle = "#c6d4e2"; ctx.font = "600 12px ui-sans-serif"; ctx.textAlign = "center";
    ctx.fillText("INPUTS", centerX.input, 22); ctx.fillText("OUTPUTS", centerX.output, 22);
    if (hasEvo) ctx.fillText("GROWN NEURONS", centerX.evolution, 22);
    ctx.fillStyle = "#7d90a6"; ctx.font = "11px ui-sans-serif";
    ctx.fillText(`${cols.input.length} · senses`, centerX.input, H - 12);
    ctx.fillText(`${cols.output.length} · answer`, centerX.output, H - 12);
    if (hasEvo) ctx.fillText(`${cols.evolution.length} · grown while training`, centerX.evolution, H - 12);
  };

  const frame = () => {
    drawStatic();
    if (flow.value) {
      for (const p of pulses) {
        p.t += p.speed;
        if (p.t >= 1) { const np = mkPulse((Math.random() * usePulseEdges.length) | 0); p.e = np.e; p.t = 0; p.speed = np.speed; }
        const { a, b } = p.e; const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2 - 20;
        const t = p.t, u = 1 - t;
        const x = u * u * a.x + 2 * u * t * mx + t * t * b.x;
        const y = u * u * a.y + 2 * u * t * my + t * t * b.y;
        ctx.globalAlpha = Math.sin(t * Math.PI) * 0.9;
        ctx.fillStyle = p.e.w > 0 ? "#8ffff0" : "#ffb4b4";
        ctx.beginPath(); ctx.arc(x, y, 1.9, 0, 7); ctx.fill();
      }
      ctx.globalAlpha = 1;
    }
    drawNodes();
    rafId = requestAnimationFrame(frame);
  };
  cancelAnimationFrame(rafId);
  frame();

  const pick = (e, maxD) => {
    const r = canvas.getBoundingClientRect();
    const sx = W / r.width; const mx = (e.clientX - r.left) * sx, my = (e.clientY - r.top) * sx;
    let best = null, bd = maxD;
    for (const p of pos.values()) { const d = (p.x - mx) ** 2 + (p.y - my) ** 2; if (d < bd) { bd = d; best = p; } }
    return best;
  };
  const onMove = (e) => {
    const r = canvas.getBoundingClientRect();
    const best = pick(e, 80);
    hover.value = best ? { ...best, mx: (e.clientX - r.left), my: (e.clientY - r.top) } : null;
  };
  const onLeave = () => { hover.value = null; };
  const onClick = (e) => {
    const best = pick(e, 320);
    emit("select", best ? best.id : null);
  };
  canvas.addEventListener("mousemove", onMove);
  canvas.addEventListener("mouseleave", onLeave);
  canvas.addEventListener("click", onClick);
  cleanupListeners = () => {
    canvas.removeEventListener("mousemove", onMove);
    canvas.removeEventListener("mouseleave", onLeave);
    canvas.removeEventListener("click", onClick);
  };
}

function teardown() {
  cancelAnimationFrame(rafId);
  if (cleanupListeners) { cleanupListeners(); cleanupListeners = null; }
}

onMounted(() => nextTick(setup));
watch(() => props.graph, () => nextTick(setup));
watch(flow, () => nextTick(setup));
onBeforeUnmount(teardown);
</script>

<template>
  <div v-if="!graph || !graph.nodes.length" class="muted center pad">No graph for this algorithm yet.</div>
  <div v-else class="canvas-wrap">
    <canvas ref="canvasRef" class="canvas-el canvas-el--pick" />
    <div v-if="hover" class="tooltip mono" :style="{ left: Math.min(hover.mx + 12, 560) + 'px', top: (hover.my + 12) + 'px' }">
      #{{ hover.id }} · {{ hover.type }} neuron · value {{ hover.value }} · <span class="muted">click to inspect</span>
    </div>
    <div class="legend center">
      <span><span class="sw sw--input" /> input</span>
      <span><span class="sw sw--output" /> output</span>
      <span><span class="sw sw--evolution" /> grown</span>
      <span><span class="sw sw--pos" /> strengthening</span>
      <span><span class="sw sw--neg" /> inhibiting</span>
      <button class="btn--tiny" @click="flow = !flow">{{ flow ? "⏸ pause signal" : "▶ play signal" }}</button>
    </div>
  </div>
</template>
