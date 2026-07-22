<script setup>
import { t } from "../i18n.js";
</script>

<template>
  <div class="prose">
    <h1>{{ t("pages.docs.title") }}</h1>
    <p class="sub">{{ t("pages.docs.sub") }}</p>

    <div class="banner ok">
      <b>{{ t("pages.docs.coreLabel") }}</b> <span v-html="t('pages.docs.core')"></span>
    </div>

    <h2>{{ t("pages.docs.h2decentralized") }}</h2>
    <p>{{ t("pages.docs.pubIntro") }}</p>
    <ul>
      <li v-html="t('pages.docs.li1')"></li>
      <li v-html="t('pages.docs.li2')"></li>
      <li v-html="t('pages.docs.li3')"></li>
    </ul>
    <p v-html="t('pages.docs.decentBody')"></p>

    <h2>{{ t("pages.docs.h2pipeline") }}</h2>
    <p v-html="t('pages.docs.pipeIntro')"></p>
    <ol>
      <li v-html="t('pages.docs.step1')"></li>
      <li v-html="t('pages.docs.step2')"></li>
      <li v-html="t('pages.docs.step3')"></li>
    </ol>
    <p class="muted" v-html="t('pages.docs.noRelay')"></p>
    <p class="muted" v-html="t('pages.docs.sample')"></p>

    <h2>{{ t("pages.docs.h2network") }}</h2>
    <p v-html="t('pages.docs.netBody')"></p>
    <p><code>docker run -d qubiclab/atlas-worker:latest</code></p>

    <h2>{{ t("pages.docs.h2scope") }}</h2>
    <p class="muted" v-html="t('pages.docs.scopeBody')"></p>

    <h2>{{ t("pages.docs.h2open") }}</h2>
    <p v-html="t('pages.docs.openBody')"></p>
    <p>
      <a class="cta" href="https://github.com/qubic-atlas/core" target="_blank" rel="noopener">{{ t("pages.docs.sourceCta") }}</a>
    </p>
    <p class="muted" v-html="t('pages.docs.openNote')"></p>

    <h2>{{ t("pages.docs.h2roadmap") }}</h2>
    <p>{{ t("pages.docs.roadmapIntro") }}</p>
    <ul>
      <li v-html="t('pages.docs.rm1')"></li>
      <li v-html="t('pages.docs.rm2')"></li>
      <li v-html="t('pages.docs.rm3')"></li>
      <li v-html="t('pages.docs.rm4')"></li>
      <li v-html="t('pages.docs.rm5')"></li>
    </ul>
    <p>
      <a href="https://github.com/qubic-atlas/core/blob/main/ROADMAP.md" target="_blank" rel="noopener">{{ t("pages.docs.roadmapCta") }}</a>
    </p>
  </div>
</template>
