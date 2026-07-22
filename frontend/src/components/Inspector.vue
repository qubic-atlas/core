<script setup>
import { computed } from "vue";
import { fmt } from "../api.js";

const COLORS = { input: "#3a9bff", output: "#00e0c6", evolution: "#c084fc" };
const SW = { input: "sw--input", output: "sw--output", evolution: "sw--evolution" };

const props = defineProps({
  graph: Object,
  id: { type: Number, default: null },
});
const emit = defineEmits(["close"]);

// Connection stats computed from the *rendered* synapse sample (graph.links).
const info = computed(() => {
  if (props.id == null) return null;
  const node = props.graph.nodes.find((n) => n.id === props.id);
  if (!node) return null;
  let inDeg = 0, outDeg = 0, pos = 0, neg = 0;
  const neighbors = new Set();
  for (const l of props.graph.links) {
    if (l.source === props.id) { outDeg++; neighbors.add(l.target); (l.weight > 0 ? (pos++) : (neg++)); }
    else if (l.target === props.id) { inDeg++; neighbors.add(l.source); (l.weight > 0 ? (pos++) : (neg++)); }
  }
  return { node, inDeg, outDeg, pos, neg, neighbors: [...neighbors] };
});

const typeName = computed(() => {
  if (!info.value) return "";
  return { input: "input", output: "output", evolution: "grown" }[info.value.node.type] || info.value.node.type;
});
const total = computed(() => (info.value ? info.value.pos + info.value.neg : 0));
const posPct = computed(() => (total.value ? (info.value.pos / total.value) * 100 : 0));
const negPct = computed(() => (total.value ? (info.value.neg / total.value) * 100 : 0));
const swClass = computed(() => (info.value ? SW[info.value.node.type] || "" : ""));
</script>

<template>
  <div v-if="!info" class="inspector">
    <div class="card-note">
      Click any neuron to inspect its wiring — in/out connections, how many strengthen vs inhibit,
      and which neurons it talks to.
    </div>
  </div>

  <div v-else class="inspector">
    <div class="inspector__head">
      <div class="inspector__id">
        <span class="sw sw--lg" :class="swClass" />
        <b class="mono">neuron #{{ info.node.id }}</b>
      </div>
      <button class="btn--chip" @click="emit('close')">clear</button>
    </div>

    <div class="kv kv--inspector mono">
      <div class="k">type</div><div class="v">{{ typeName }}</div>
      <div class="k">value</div><div class="v">{{ info.node.value }}</div>
      <div class="k">in-degree</div><div class="v">{{ fmt(info.inDeg) }}</div>
      <div class="k">out-degree</div><div class="v">{{ fmt(info.outDeg) }}</div>
    </div>

    <div class="insp-section">
      <div class="muted insp-label">connection mix</div>
      <div class="mixbar">
        <div class="mixbar__pos" :style="{ width: posPct + '%' }" />
        <div class="mixbar__neg" :style="{ width: negPct + '%' }" />
      </div>
      <div class="mixbar__labels">
        <span class="txt-syn-pos">{{ fmt(info.pos) }} strengthening</span>
        <span class="txt-syn-neg">{{ fmt(info.neg) }} inhibiting</span>
      </div>
    </div>

    <div class="insp-section">
      <div class="muted insp-label">connected neurons ({{ fmt(info.neighbors.length) }})</div>
      <div class="mono chip-ids">
        <span v-for="nid in info.neighbors.slice(0, 14)" :key="nid" class="chip-id">#{{ nid }}</span>
        <span v-if="info.neighbors.length > 14" class="muted chip-ids__more">+{{ fmt(info.neighbors.length - 14) }} more</span>
      </div>
    </div>

    <div class="inspector__foot">
      Based on {{ fmt(graph.renderedLinks ?? graph.links.length) }} of {{ fmt(graph.totalLinks) }} synapses{{ graph.truncatedLinks ? " (sampled)" : "" }} — true degree is higher.
    </div>
  </div>
</template>
