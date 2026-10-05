<script setup lang="ts">
// Display settings in the top bar: the theme (the system's, light or dark) and the language (Japanese or English), remembered for
// the next visit (prefs.ts). A disclosure: the button opens a small panel of two radio groups; Escape or a click elsewhere closes it.
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { t } from "../i18n.js";
import { locales, prefs, setLocale, setTheme, themes, type Locale, type Theme } from "../prefs.js";
import Icon from "./Icon.vue";

const open = ref(false);
const root = ref<HTMLElement>();
const button = ref<HTMLButtonElement>();
const panel = ref<HTMLElement>();

// each language is named in itself (WCAG 3.1.2: the part's language is marked)
const languageNames: Readonly<Record<Locale, string>> = { ja: "日本語", en: "English" };
const themeIcons: Readonly<Record<Theme, string>> = { system: "monitor", light: "sun", dark: "moon" };
const icon = computed(() => themeIcons[prefs.theme]);

async function toggle(): Promise<void> {
  open.value = !open.value;
  if (open.value) {
    await nextTick();
    panel.value?.querySelector<HTMLInputElement>("input:checked")?.focus();
  }
}

function onPointer(event: PointerEvent): void {
  if (open.value && root.value !== undefined && !root.value.contains(event.target as Node)) {
    open.value = false;
  }
}

function onKey(event: KeyboardEvent): void {
  if (event.key === "Escape" && open.value) {
    // the page's own Escape (closing Authorize, leaving the search) does not see this one
    event.stopPropagation();
    open.value = false;
    button.value?.focus();
  }
}

onMounted(() => document.addEventListener("pointerdown", onPointer));
onBeforeUnmount(() => document.removeEventListener("pointerdown", onPointer));
</script>

<template>
  <div ref="root" class="settings" @keydown="onKey">
    <button
      ref="button"
      type="button"
      class="btn icon settings-button"
      :aria-expanded="open"
      aria-controls="settings-panel"
      :aria-label="t().displaySettings"
      :title="t().displaySettings"
      @click="toggle"
    >
      <Icon :name="icon" :size="16" />
    </button>
    <div v-if="open" id="settings-panel" ref="panel" class="settings-panel" role="group" :aria-label="t().displaySettings">
      <fieldset class="settings-group">
        <legend>{{ t().theme }}</legend>
        <div class="segmented">
          <label v-for="th in themes" :key="th" :class="{ on: prefs.theme === th }">
            <input type="radio" name="tisilia-theme" :value="th" :checked="prefs.theme === th" @change="setTheme(th)" />
            <Icon :name="themeIcons[th]" :size="14" />{{ t().themeNames[th] }}
          </label>
        </div>
      </fieldset>
      <fieldset class="settings-group">
        <legend>{{ t().language }}</legend>
        <div class="segmented">
          <label v-for="l in locales" :key="l" :class="{ on: prefs.locale === l }" :lang="l">
            <input type="radio" name="tisilia-locale" :value="l" :checked="prefs.locale === l" @change="setLocale(l)" />{{ languageNames[l] }}
          </label>
        </div>
      </fieldset>
    </div>
  </div>
</template>
