<script setup>
import { ref, onMounted, onBeforeUnmount } from "vue";
import { locale, setLocale, LOCALES, t } from "../i18n.js";

const open = ref(false);
const root = ref(null);

function pick(code) { setLocale(code); open.value = false; }
function onDocClick(e) { if (root.value && !root.value.contains(e.target)) open.value = false; }

onMounted(() => document.addEventListener("click", onDocClick));
onBeforeUnmount(() => document.removeEventListener("click", onDocClick));
</script>

<template>
  <div class="lang" ref="root">
    <button class="lang__btn" :title="t('lang')" @click="open = !open">
      <span class="lang__globe">🌐</span>
      <span class="lang__code">{{ locale.toUpperCase() }}</span>
    </button>
    <ul v-if="open" class="lang__menu">
      <li v-for="l in LOCALES" :key="l.code">
        <button class="lang__item" :class="{ active: l.code === locale }" @click="pick(l.code)">
          {{ l.label }}
        </button>
      </li>
    </ul>
  </div>
</template>
