<script setup>
import { ref, onMounted, onBeforeUnmount } from "vue";
import { api, fmt } from "../api.js";
import { t } from "../i18n.js";
import LangSwitcher from "./LangSwitcher.vue";

const tick = ref(null);
let timer = 0;
let live = true;

const load = () => api.tick().then((d) => { if (live) tick.value = d.tickInfo; }).catch(() => {});

onMounted(() => { load(); timer = setInterval(load, 3000); });
onBeforeUnmount(() => { live = false; clearInterval(timer); });
</script>

<template>
  <header class="top">
    <div class="wrap topbar">
      <router-link to="/" class="brand"><span class="dot" /> Qubic&nbsp;Atlas</router-link>
      <nav class="main">
        <router-link to="/" :class="{ active: $route.path === '/' }">{{ t("nav.overview") }}</router-link>
        <router-link to="/proofs" :class="{ active: $route.path.startsWith('/proofs') }">{{ t("nav.proofs") }}</router-link>
        <router-link to="/epochs" :class="{ active: $route.path.startsWith('/epochs') }">{{ t("nav.epochs") }}</router-link>
        <router-link to="/computors" :class="{ active: $route.path.startsWith('/computors') }">{{ t("nav.computors") }}</router-link>
        <router-link to="/network" :class="{ active: $route.path.startsWith('/network') }">{{ t("nav.network") }}</router-link>
        <router-link to="/docs" :class="{ active: $route.path.startsWith('/docs') }">{{ t("nav.docs") }}</router-link>
      </nav>
      <div class="live">
        <span class="pill"><span class="blink" /> Epoch <b>{{ tick ? tick.epoch : "—" }}</b></span>
        <span class="pill">Tick <b>{{ tick ? fmt(tick.tick) : "—" }}</b></span>
      </div>
      <LangSwitcher />
    </div>
  </header>
</template>
