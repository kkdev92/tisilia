<script setup lang="ts">
import mascotLight from "../assets/brand/tisilia/mascot-chibi-light.jpg";
import mascotDark from "../assets/brand/tisilia/mascot-chibi-dark.jpg";
import { prefs } from "../prefs.js";
</script>

<template>
  <picture class="empty-mascot">
    <source :media="prefs.theme === 'system' ? '(prefers-color-scheme: dark)' : prefs.theme === 'dark' ? 'all' : 'not all'" :srcset="mascotDark" />
    <img :src="mascotLight" alt="" width="144" height="144" />
  </picture>
</template>

<style scoped>
.empty-mascot {
  display: block;
  width: 144px;
  margin: 16px auto 0;
  overflow: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius);
}
.empty-mascot img {
  display: block;
  width: 100%;
  height: auto;
  object-fit: contain;
}
</style>
