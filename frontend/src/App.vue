<script setup>
import Header from "./components/Header.vue";
import BetaBanner from "./components/BetaBanner.vue";
import Footer from "./components/Footer.vue";
</script>

<template>
  <BetaBanner />
  <Header />
  <main class="wrap">
    <router-view />
  </main>
  <Footer />
</template>
