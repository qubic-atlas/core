<script setup>
import { t } from "../i18n.js";
</script>

<template>
  <footer class="site-footer">
    <div class="wrap site-footer__inner">
      <div class="site-footer__brand">
        <span class="brand"><span class="dot" /> Qubic&nbsp;Atlas</span>
        <p class="site-footer__tagline">{{ t("footer.tagline") }}</p>
      </div>

      <nav class="site-footer__col">
        <h4 class="site-footer__h">{{ t("footer.resources") }}</h4>
        <a href="https://docs.qubic.org" target="_blank" rel="noopener">{{ t("footer.documentation") }}</a>
        <a href="https://qubic.org" target="_blank" rel="noopener">{{ t("footer.website") }}</a>
      </nav>

      <nav class="site-footer__col">
        <h4 class="site-footer__h">{{ t("footer.community") }}</h4>
        <a href="https://join.qubic.org/discord" target="_blank" rel="noopener">{{ t("footer.discord") }}</a>
        <a href="https://x.com/_qubic_" target="_blank" rel="noopener">{{ t("footer.x") }}</a>
      </nav>
    </div>
    <div class="wrap site-footer__foot">
      <span class="site-footer__disclaimer">{{ t("footer.disclaimer") }}</span>
    </div>
  </footer>
</template>
