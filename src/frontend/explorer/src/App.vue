<script setup lang="ts">
// Tisilia Explorer: the API's operations on one page, grouped by tag, each run through the same runtime as the
// generated clients. The interaction most API explorers share — operations listed by tag and coloured by method, opened in place,
// executed with the response right beside the inputs, Authorize at the top — with fewer steps: inputs are live at once, the search
// and Authorize stay in reach while scrolling, and Enter in the search opens the first match.
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import AuthDialog from "./components/AuthDialog.vue";
import Icon from "./components/Icon.vue";
import EmptyMascot from "./components/EmptyMascot.vue";
import OperationBlock from "./components/OperationBlock.vue";
import SchemasSection from "./components/SchemasSection.vue";
import SettingsMenu from "./components/SettingsMenu.vue";
import { describeCredential } from "./auth.js";
import { filterOperations } from "./explorer.js";
import { t } from "./i18n.js";
import { authorized, load, openOperation, store } from "./state.js";

const searchInput = ref<HTMLInputElement>();

const matching = computed(() => filterOperations(store.summaries, store.filter, undefined));
const searching = computed(() => store.filter.trim() !== "");

/** Operations by tag, in the contract's order; an operation without tags is listed under "default". */
const groups = computed(() => {
  const ops = store.model?.operations ?? [];
  const shown = new Set(matching.value.map((s) => s.id));
  const byTag = new Map<string, typeof ops[number][]>();
  for (const op of ops) {
    if (!shown.has(op.id)) {
      continue;
    }
    for (const tag of op.tags.length > 0 ? op.tags : ["default"]) {
      const list = byTag.get(tag) ?? [];
      list.push(op);
      byTag.set(tag, list);
    }
  }
  return [...byTag].map(([tag, list]) => ({ tag, ops: list }));
});
const authTitle = computed(() => (authorized.value ? t().authorizedWith(store.credentials.map(describeCredential).join(", ")) : t().authorizeHint));

function toggleGroup(tag: string): void {
  store.closedTags[tag] = store.closedTags[tag] !== true;
}

/** Enter opens the first operation shown; Escape clears the search (and leaves it on a second press). */
function onSearchKey(event: KeyboardEvent): void {
  if (event.key === "Enter") {
    const first = groups.value[0]?.ops[0];
    if (first !== undefined) {
      event.preventDefault();
      openOperation(first.id);
    }
  } else if (event.key === "Escape") {
    if (store.filter !== "") {
      store.filter = "";
    } else {
      searchInput.value?.blur();
    }
  }
}

function typing(target: EventTarget | null): boolean {
  const el = target as HTMLElement | null;
  return el !== null && (el.tagName === "INPUT" || el.tagName === "TEXTAREA" || el.tagName === "SELECT" || el.isContentEditable);
}

function onKey(event: KeyboardEvent): void {
  if (event.key === "/" && !typing(event.target) && !store.authOpen) {
    event.preventDefault();
    searchInput.value?.focus();
  } else if (event.key === "Escape") {
    store.authOpen = false;
  }
}

onMounted(() => {
  window.addEventListener("keydown", onKey);
  void load();
});
onBeforeUnmount(() => window.removeEventListener("keydown", onKey));
</script>

<template>
  <div class="app">
    <div v-if="store.loading" class="app-state"><span class="spinner" /><span>{{ t().loading }}</span></div>

    <div v-else-if="store.loadError" class="page">
      <div class="notice bad app-error">
        <Icon name="alert" :size="16" />
        <div class="notice-body"><strong>{{ t().loadFailed }}</strong><p class="mono">{{ store.loadError }}</p></div>
      </div>
    </div>

    <template v-else-if="store.model">
      <header class="topbar">
        <div class="page topbar-inner">
          <span class="brand" :title="'Tisilia Explorer · ' + store.model.document.apiId">
            <span class="brand-mark"><Icon name="braces" :size="15" /></span>
            <span class="brand-name">{{ store.model.document.apiId }}</span>
          </span>
          <div class="search">
            <Icon class="search-icon" name="search" :size="15" />
            <input
              ref="searchInput"
              v-model="store.filter"
              class="search-input"
              type="search"
              :placeholder="t().searchPlaceholder"
              :aria-label="t().searchLabel"
              @keydown="onSearchKey"
            />
            <span v-if="searching" class="search-count">{{ matching.length }} / {{ store.summaries.length }}</span>
            <span v-else class="kbd search-key" :title="t().searchKey">/</span>
          </div>
          <button type="button" class="btn auth-button" :class="{ authorized }" :title="authTitle" @click="store.authOpen = true">
            <Icon :name="authorized ? 'lock' : 'unlock'" :size="15" /><span class="auth-label">{{ authorized ? t().authorized : t().authorize }}</span>
          </button>
          <SettingsMenu />
        </div>
      </header>

      <main class="page">
        <section class="intro">
          <h1 class="intro-title">{{ store.model.document.apiId }}<span class="chip mono" :title="t().apiVersion">{{ store.model.document.version }}</span></h1>
          <p class="intro-meta">
            <span>{{ t().baseUrl }} <code>{{ store.apiBase }}</code></span>
            <span>{{ t().operationCount(store.summaries.length) }}</span>
            <a :href="store.contractUrl" target="_blank" rel="noopener">{{ t().contractJson }}</a>
          </p>
          <p class="intro-text">{{ t().intro }}</p>
        </section>

        <div v-if="groups.length === 0" class="search-empty">
          <p>{{ t().noMatch(store.filter) }} <button type="button" class="btn small ghost" @click="store.filter = ''">{{ t().clearSearch }}</button></p>
          <EmptyMascot />
        </div>

        <section v-for="g in groups" :key="g.tag" class="group" :class="{ open: store.closedTags[g.tag] !== true }">
          <h2 class="group-head">
            <button type="button" class="group-toggle" :aria-expanded="store.closedTags[g.tag] !== true" @click="toggleGroup(g.tag)">
              <Icon class="chev" :class="{ open: store.closedTags[g.tag] !== true }" name="chevron" :size="16" />
              <span class="group-name">{{ g.tag }}</span>
              <span class="group-count">{{ g.ops.length }}</span>
            </button>
          </h2>
          <div v-if="store.closedTags[g.tag] !== true" class="group-ops">
            <OperationBlock v-for="op in g.ops" :key="g.tag + '/' + op.id" :op="op" :tag="g.tag" />
          </div>
        </section>

        <SchemasSection />
      </main>
    </template>

    <AuthDialog v-if="store.authOpen" @close="store.authOpen = false" />
    <div v-if="store.toast" class="toast" role="status">
      {{ store.toast.text }}
      <pre v-if="store.toast.detail">{{ store.toast.detail }}</pre>
    </div>
  </div>
</template>
