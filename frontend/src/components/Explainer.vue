<script setup>
import { ref, computed } from "vue";

const props = defineProps({ algorithm: String });

const TASK = {
  HyperIdentity: {
    tag: "the identity task",
    plain: "The network is trained to reproduce a target pattern at its outputs — a memory/identity test. The score counts how many of the 512 output neurons end up correct.",
  },
  Addition: {
    tag: "the addition task",
    plain: "The network is trained to add binary numbers — it learns arithmetic from examples. The score measures how many test cases it gets right across every training sample.",
  },
};

const open = ref(true);
const t = computed(() => TASK[props.algorithm] || TASK.HyperIdentity);
</script>

<template>
  <div class="panel pad explainer">
    <div class="explainer__head" @click="open = !open">
      <h3>What is Qubic computing here?</h3>
      <span class="explainer__toggle">{{ open ? "hide ▲" : "show ▼" }}</span>
    </div>
    <template v-if="open">
      <p class="explainer__lede">
        Qubic's proof-of-work isn't hashing — it's <b>training a tiny neural network</b>. A miner is handed a random
        starting network (derived from their <span class="mono">nonce</span>) and must improve it at a set task.
        The better the final network, the higher the score. Cross the threshold and it's a valid proof.
        This page rebuilt that exact network and the search that produced it, from public data.
      </p>
      <div class="stats stats--explainer">
        <div class="stat estep--blue">
          <div class="estep__head"><span class="estep__num">1</span><b class="estep__title">Start random</b></div>
          <div class="estep__body">A network is seeded from the miner's nonce — random synapses, no skill yet.</div>
        </div>
        <div class="stat estep--teal">
          <div class="estep__head"><span class="estep__num">2</span><b class="estep__title">Mutate &amp; keep what helps</b></div>
          <div class="estep__body">One synapse is nudged at a time. If the score improves, keep it; if not, discard it. Repeat hundreds of times.</div>
        </div>
        <div class="stat estep--purple">
          <div class="estep__head"><span class="estep__num">3</span><b class="estep__title">Score vs threshold</b></div>
          <div class="estep__body">The trained network is graded on {{ t.tag }}. Score ≥ threshold ⇒ a valid, verifiable proof.</div>
        </div>
      </div>
      <p class="explainer__task">
        <b class="txt-body">This proof's task:</b> {{ t.plain }}
      </p>
    </template>
  </div>
</template>
