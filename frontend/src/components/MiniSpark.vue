<script setup>
import { ref, onMounted, watch } from "vue";

// Tiny "how the miner found it" sparkline: the best-score curve ratcheting up to
// (and past) the threshold. Used inline in the proofs list for confirmed proofs.
const props = defineProps({
  spark: { type: Array, required: true },   // downsampled best-score values
  threshold: { type: Number, default: 0 },
});
const cv = ref(null);

function draw() {
  const c = cv.value;
  if (!c || !props.spark?.length) return;
  const W = 96, H = 24, dpr = window.devicePixelRatio || 1;
  c.width = W * dpr; c.height = H * dpr;
  const ctx = c.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, W, H);
  const vals = props.spark;
  const lo = Math.min(...vals, props.threshold || Infinity);
  const hi = Math.max(...vals, props.threshold || -Infinity);
  const span = hi - lo || 1;
  const X = (i) => 1 + (i / Math.max(1, vals.length - 1)) * (W - 2);
  const Y = (v) => H - 2 - ((v - lo) / span) * (H - 4);
  // threshold line
  if (props.threshold) {
    ctx.strokeStyle = "#fbbf24"; ctx.globalAlpha = 0.5; ctx.setLineDash([3, 3]);
    ctx.beginPath(); ctx.moveTo(0, Y(props.threshold)); ctx.lineTo(W, Y(props.threshold)); ctx.stroke();
    ctx.setLineDash([]); ctx.globalAlpha = 1;
  }
  // best-score curve
  ctx.strokeStyle = "#00e0c6"; ctx.lineWidth = 1.5; ctx.beginPath();
  vals.forEach((v, i) => (i ? ctx.lineTo(X(i), Y(v)) : ctx.moveTo(X(i), Y(v))));
  ctx.stroke();
  // end dot
  ctx.fillStyle = "#8ffff0";
  ctx.beginPath(); ctx.arc(X(vals.length - 1), Y(vals[vals.length - 1]), 1.6, 0, 7); ctx.fill();
}

onMounted(draw);
watch(() => props.spark, draw);
</script>

<template>
  <canvas ref="cv" class="minispark" title="How the miner found it — best score over the mutation search" />
</template>
