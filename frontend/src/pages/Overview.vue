<script setup>
import { ref, onMounted, onBeforeUnmount } from "vue";
import { api, fmt, FEATURED } from "../api.js";
import { t } from "../i18n.js";
import HeroNet from "../components/HeroNet.vue";

const tick = ref(null);
const stats = ref(null);
let timer = 0, live = true;

const load = () => {
  api.tick().then((d) => { if (live) tick.value = d.tickInfo; }).catch(() => {});
  api.verificationsStats().then((d) => { if (live) stats.value = d; }).catch(() => {});
};

onMounted(() => { load(); timer = setInterval(load, 5000); });
onBeforeUnmount(() => { live = false; clearInterval(timer); });
</script>

<template>
  <!-- hero -->
  <div class="panel hero">
    <HeroNet />
    <div class="pad hero__body">
      <div class="chip hi hero__badge">{{ t("pages.overview.badge") }}</div>
      <h1 class="hero__title">{{ t("pages.overview.title") }}</h1>
      <p class="hero__lede" v-html="t('pages.overview.lede')"></p>
      <div class="hero__cta">
        <router-link to="/proofs"><button class="primary btn--lg">{{ t("pages.overview.exploreProofs") }}</button></router-link>
        <router-link :to="`/proofs/${FEATURED}?auto=1`"><button class="btn--lg">{{ t("pages.overview.seeNetwork") }}</button></router-link>
      </div>
    </div>
  </div>

  <!-- live stats -->
  <div class="stats stats--live mb-26">
    <div class="stat"><div class="k">{{ t("pages.overview.statEpoch") }}</div><div class="v stat__v--accent">{{ tick ? tick.epoch : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.overview.statTick") }}</div><div class="v">{{ tick ? fmt(tick.tick) : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.overview.statVerified") }}</div><div class="v">{{ stats ? fmt(stats.confirmed) : "—" }}</div></div>
    <div class="stat"><div class="k">{{ t("pages.overview.statRate") }}</div><div class="v stat__v--good">{{ stats ? fmt(stats.ratePerMin) + " " + t("pages.overview.perMin") : "—" }}</div></div>
  </div>

  <!-- concept -->
  <h2 class="section-title">{{ t("pages.overview.stepsTitle") }}</h2>
  <p class="sub">{{ t("pages.overview.stepsSub") }}</p>
  <div class="stats stats--steps mb-28">
    <div class="panel pad step-card step-card--blue">
      <div class="step-card__head"><span class="step-card__num">1</span><b>{{ t("pages.overview.s1t") }}</b></div>
      <div class="step-card__body" v-html="t('pages.overview.s1b')"></div>
    </div>
    <div class="panel pad step-card step-card--teal">
      <div class="step-card__head"><span class="step-card__num">2</span><b>{{ t("pages.overview.s2t") }}</b></div>
      <div class="step-card__body">{{ t("pages.overview.s2b") }}</div>
    </div>
    <div class="panel pad step-card step-card--purple">
      <div class="step-card__head"><span class="step-card__num">3</span><b>{{ t("pages.overview.s3t") }}</b></div>
      <div class="step-card__body">{{ t("pages.overview.s3b") }}</div>
    </div>
  </div>

  <!-- what you can do -->
  <div class="grid2 mb-10">
    <div class="panel pad">
      <h3>{{ t("pages.overview.do1t") }}</h3>
      <p class="card-note" v-html="t('pages.overview.do1b')"></p>
      <router-link to="/proofs"><button class="btn--sm">{{ t("pages.overview.do1cta") }}</button></router-link>
    </div>
    <div class="panel pad">
      <h3>{{ t("pages.overview.do2t") }}</h3>
      <p class="card-note" v-html="t('pages.overview.do2b')"></p>
      <router-link :to="`/proofs/${FEATURED}?auto=1`"><button class="btn--sm">{{ t("pages.overview.do2cta") }}</button></router-link>
    </div>
  </div>
</template>
