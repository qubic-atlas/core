<script setup>
import { ref, computed } from "vue";
import { useRoute } from "vue-router";
import NeuralGraph from "./NeuralGraph.vue";
import RadialView from "./RadialView.vue";
import MatrixView from "./MatrixView.vue";
import Inspector from "./Inspector.vue";

const props = defineProps({ graph: Object });

const VIEWS = [
  { id: "flow", label: "Flow", hint: "signals moving through the network" },
  { id: "radial", label: "Radial", hint: "neurons on a ring, synapses as chords" },
  { id: "matrix", label: "Matrix", hint: "every synapse as a weight heatmap" },
];

const route = useRoute();
const initial = route.query.view;
const view = ref(VIEWS.some((v) => v.id === initial) ? initial : "flow");
const selected = ref(null);

const active = computed(() => VIEWS.find((v) => v.id === view.value));
const inspectable = computed(() => view.value === "flow" || view.value === "radial");
</script>

<template>
  <div>
    <div class="view-switch">
      <button v-for="v in VIEWS" :key="v.id" class="view-switch__tab"
        :class="{ 'view-switch__tab--active': view === v.id }" @click="view = v.id">
        {{ v.label }}
      </button>
      <span class="view-switch__hint">{{ active.hint }}</span>
    </div>
    <div :class="{ 'annview-layout': inspectable }">
      <div class="annview-canvas-col">
        <NeuralGraph v-if="view === 'flow'" :graph="graph" :selected="selected" @select="selected = $event" />
        <RadialView v-else-if="view === 'radial'" :graph="graph" :selected="selected" @select="selected = $event" />
        <MatrixView v-else-if="view === 'matrix'" :graph="graph" />
      </div>
      <Inspector v-if="inspectable" :graph="graph" :id="selected" @close="selected = null" />
    </div>
  </div>
</template>
