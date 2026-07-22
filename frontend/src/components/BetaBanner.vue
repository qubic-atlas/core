<script setup>
import { ref } from "vue";
import { t } from "../i18n.js";

const KEY = "atlas-beta-dismissed";
let dismissed0 = false;
try { dismissed0 = localStorage.getItem(KEY) === "1"; } catch { /* ignore */ }
const show = ref(!dismissed0);

function dismiss() {
  show.value = false;
  try { localStorage.setItem(KEY, "1"); } catch { /* ignore */ }
}
</script>

<template>
  <div v-if="show" class="beta-banner" role="status">
    <div class="wrap beta-banner__inner">
      <span class="beta-banner__tag">{{ t("beta.tag") }}</span>
      <span class="beta-banner__text">{{ t("beta.text") }}</span>
      <button class="beta-banner__close" :aria-label="'Dismiss'" @click="dismiss">×</button>
    </div>
  </div>
</template>
