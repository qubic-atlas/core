<script setup>
import { ref, watch, onMounted, onBeforeUnmount, nextTick, computed } from "vue";

// Weight-density heatmap over the WHOLE network (downsampled GxG). Rows = source
// blocks, cols = target blocks. Teal = net strengthening, red = net inhibiting.
// Ported verbatim from the React reference.
const props = defineProps({ graph: Object });

const ref_ = ref(null);
const hover = ref(null);
const m = computed(() => props.graph?.matrix);
const totalLinks = computed(() => props.graph?.totalLinks);
let cleanup = null;

function draw() {
  if (cleanup) { cleanup(); cleanup = null; }
  const mm = m.value;
  if (!mm) return;
  const c = ref_.value;
  if (!c) return;

  const G = mm.g, cells = mm.cells;
  const size = 460, dpr = window.devicePixelRatio || 1, pad = 40;
  c.width = (size + pad) * dpr; c.height = (size + pad) * dpr;
  c.style.width = "100%"; c.style.maxWidth = size + pad + "px"; c.style.height = "auto";
  const ctx = c.getContext("2d"); ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, size + pad, size + pad);
  const max = Math.max(1, ...cells.map((v) => Math.abs(v)));
  const cw = size / G;
  for (let r = 0; r < G; r++) for (let col = 0; col < G; col++) {
    const v = cells[r * G + col]; if (!v) continue;
    const t = Math.min(1, Math.abs(v) / max);
    ctx.fillStyle = v > 0 ? `rgba(0,224,198,${0.12 + t * 0.88})` : `rgba(248,113,113,${0.12 + t * 0.88})`;
    ctx.fillRect(pad + col * cw, pad + r * cw, Math.ceil(cw), Math.ceil(cw));
  }
  ctx.fillStyle = "#7d90a6"; ctx.font = "11px ui-sans-serif";
  ctx.save(); ctx.translate(12, pad + size / 2); ctx.rotate(-Math.PI / 2); ctx.textAlign = "center"; ctx.fillText("source neuron →", 0, 0); ctx.restore();
  ctx.textAlign = "center"; ctx.fillText("target neuron →", pad + size / 2, 22);
  ctx.strokeStyle = "#22344a"; ctx.strokeRect(pad, pad, size, size);

  const onMove = (e) => {
    const rect = c.getBoundingClientRect(); const sx = (size + pad) / rect.width;
    const mx = (e.clientX - rect.left) * sx - pad, my = (e.clientY - rect.top) * sx - pad;
    if (mx < 0 || my < 0 || mx > size || my > size) { hover.value = null; return; }
    const col = Math.floor(mx / cw), r = Math.floor(my / cw);
    hover.value = { mx: e.clientX - rect.left, my: e.clientY - rect.top, v: cells[r * G + col], r, col };
  };
  const onLeave = () => { hover.value = null; };
  c.addEventListener("mousemove", onMove); c.addEventListener("mouseleave", onLeave);
  cleanup = () => {
    c.removeEventListener("mousemove", onMove);
    c.removeEventListener("mouseleave", onLeave);
  };
}

onMounted(() => nextTick(draw));
watch(m, () => nextTick(draw));
onBeforeUnmount(() => { if (cleanup) cleanup(); });
</script>

<template>
  <div v-if="!m" class="muted center pad">Matrix not available for this reconstruction.</div>
  <div v-else class="canvas-wrap">
    <canvas ref="ref_" class="canvas-el" />
    <div v-if="hover && hover.v != null" class="tooltip mono" :style="{ left: Math.min(hover.mx + 12, 460) + 'px', top: (hover.my + 12) + 'px' }">
      net weight {{ hover.v > 0 ? "+" : "" }}{{ hover.v }}
    </div>
    <div class="legend center">
      <span><span class="sw sw--pos" /> net strengthening</span>
      <span><span class="sw sw--neg" /> net inhibiting</span>
      <span class="muted">brighter = stronger · computed over all {{ totalLinks?.toLocaleString?.() || "" }} synapses</span>
    </div>
  </div>
</template>
