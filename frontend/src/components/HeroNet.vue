<script setup>
import { ref, onMounted, onBeforeUnmount } from "vue";

// Decorative animated mini-network for the landing hero: drifting neurons with
// signal pulses flowing between them. Purely cosmetic. Drawing logic is ported
// verbatim from the React reference (framework-agnostic canvas JS).
const canvas = ref(null);
let raf = 0;
let onResize = null;

onMounted(() => {
  const c = canvas.value, dpr = window.devicePixelRatio || 1;
  const resize = () => { c.width = c.clientWidth * dpr; c.height = c.clientHeight * dpr; };
  resize();
  const ctx = c.getContext("2d");
  const W = () => c.clientWidth, H = () => c.clientHeight;
  const N = 46;
  const nodes = Array.from({ length: N }, (_, i) => ({
    x: Math.random(), y: Math.random(),
    vx: (Math.random() - 0.5) * 0.0006, vy: (Math.random() - 0.5) * 0.0006,
    type: i % 7 === 0 ? "e" : i % 2 ? "i" : "o",
  }));
  const col = { i: "#3a9bff", o: "#00e0c6", e: "#c084fc" };
  const edges = [];
  for (let i = 0; i < N; i++) for (let j = i + 1; j < N; j++) if (Math.random() < 0.05) edges.push([i, j, Math.random() < 0.5]);
  const pulses = edges.slice(0, 30).map((e, k) => ({ e, t: (k * 0.13) % 1, sp: 0.004 + Math.random() * 0.01 }));

  const frame = () => {
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, W(), H());
    for (const n of nodes) {
      n.x += n.vx; n.y += n.vy;
      if (n.x < 0 || n.x > 1) n.vx *= -1;
      if (n.y < 0 || n.y > 1) n.vy *= -1;
    }
    ctx.lineWidth = 1;
    for (const [a, b, pos] of edges) {
      const na = nodes[a], nb = nodes[b];
      ctx.globalAlpha = 0.12; ctx.strokeStyle = pos ? "#00e0c6" : "#f87171";
      ctx.beginPath(); ctx.moveTo(na.x * W(), na.y * H()); ctx.lineTo(nb.x * W(), nb.y * H()); ctx.stroke();
    }
    for (const p of pulses) {
      p.t += p.sp; if (p.t >= 1) p.t = 0;
      const [a, b, pos] = p.e; const na = nodes[a], nb = nodes[b];
      const x = (na.x + (nb.x - na.x) * p.t) * W(), y = (na.y + (nb.y - na.y) * p.t) * H();
      ctx.globalAlpha = Math.sin(p.t * Math.PI) * 0.9; ctx.fillStyle = pos ? "#8ffff0" : "#ffb4b4";
      ctx.beginPath(); ctx.arc(x, y, 2, 0, 7); ctx.fill();
    }
    ctx.globalAlpha = 1;
    for (const n of nodes) { ctx.fillStyle = col[n.type]; ctx.beginPath(); ctx.arc(n.x * W(), n.y * H(), 2.6, 0, 7); ctx.fill(); }
    raf = requestAnimationFrame(frame);
  };
  frame();
  onResize = resize;
  window.addEventListener("resize", resize);
});

onBeforeUnmount(() => {
  cancelAnimationFrame(raf);
  if (onResize) window.removeEventListener("resize", onResize);
});
</script>

<template>
  <canvas ref="canvas" class="heronet" />
</template>
