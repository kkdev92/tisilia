<script setup lang="ts">
// Authorize: Bearer tokens, Basic, API keys and other credential headers for this page's calls. Kept in memory only —
// never stored, never in URLs, never shown in code snippets or history — and forgotten on reload or sign-out. Cookies are the
// browser's: there is no Cookie field, a cookie-authenticated API works once the browser is signed in to the application.
import { computed, nextTick, onMounted, reactive, ref } from "vue";
import { credentialProblem, describeCredential, type Credential } from "../auth.js";
import { t } from "../i18n.js";
import { authorize, signOut, store } from "../state.js";
import Icon from "./Icon.vue";

const emit = defineEmits<{ close: [] }>();

const bearer = store.credentials.find((c): c is Extract<Credential, { kind: "bearer" }> => c.kind === "bearer");
const basic = store.credentials.find((c): c is Extract<Credential, { kind: "basic" }> => c.kind === "basic");
const form = reactive({
  authorization: (bearer ? "bearer" : basic ? "basic" : store.authHints.some((h) => h.kind === "basic") && !store.authHints.some((h) => h.kind === "bearer") ? "basic" : "bearer") as "bearer" | "basic",
  token: bearer?.token ?? "",
  username: basic?.username ?? "",
  password: basic?.password ?? "",
  apiKeys: store.credentials.filter((c): c is Extract<Credential, { kind: "api-key" }> => c.kind === "api-key").map((c) => ({ header: c.header, value: c.value })),
  headers: store.credentials.filter((c): c is Extract<Credential, { kind: "header" }> => c.kind === "header").map((c) => ({ name: c.name, value: c.value })),
});
const showSecret = ref(false);
const first = ref<HTMLInputElement>();

const credentials = computed<Credential[]>(() => {
  const list: Credential[] = [];
  if (form.authorization === "bearer" && form.token.trim().length > 0) {
    list.push({ kind: "bearer", token: form.token.trim() });
  }
  if (form.authorization === "basic" && (form.username.length > 0 || form.password.length > 0)) {
    list.push({ kind: "basic", username: form.username, password: form.password });
  }
  for (const k of form.apiKeys) {
    if (k.header.trim().length > 0 || k.value.length > 0) {
      list.push({ kind: "api-key", header: k.header.trim(), value: k.value });
    }
  }
  for (const h of form.headers) {
    if (h.name.trim().length > 0 || h.value.length > 0) {
      list.push({ kind: "header", name: h.name.trim(), value: h.value });
    }
  }
  return list;
});
const problems = computed(() => credentials.value.map((c) => ({ c, problem: credentialProblem(c) })).filter((x) => x.problem !== undefined));
const protectedCount = computed(() => store.summaries.filter((s) => s.authRequired).length);
const cookieHint = computed(() => store.authHints.some((h) => h.kind === "cookie"));
const browserManaged = computed(() => store.authHints.filter((h) => h.kind === "negotiate" || h.kind === "certificate"));

function apply(): void {
  if (problems.value.length > 0) {
    return;
  }
  authorize(credentials.value);
  emit("close");
}

function forget(): void {
  signOut();
  emit("close");
}

function addApiKey(): void {
  form.apiKeys.push({ header: store.authHints.find((h) => h.kind === "api-key")?.scheme ?? "X-API-Key", value: "" });
}

onMounted(() => void nextTick(() => first.value?.focus()));
</script>

<template>
  <div class="overlay" @mousedown.self="emit('close')" @keydown.esc="emit('close')">
    <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="auth-title">
      <div class="dialog-head">
        <Icon name="shield" :size="18" />
        <h2 id="auth-title">{{ t().authorizeTitle }}</h2>
        <span class="grow" />
        <button type="button" class="btn icon ghost" :title="t().close" @click="emit('close')"><Icon name="x" :size="16" /></button>
      </div>

      <form class="dialog-form" autocomplete="off" @submit.prevent="apply">
      <div class="dialog-body">
        <div class="notice info">
          <Icon name="info" :size="16" />
          <div class="notice-body">
            <p>{{ t().credentialsMemoryOnly }}</p>
            <p v-if="protectedCount > 0">{{ t().protectedOperations(protectedCount)[0] }}<strong>{{ protectedCount }}</strong>{{ t().protectedOperations(protectedCount)[1] }}</p>
          </div>
        </div>

        <div v-if="store.authHints.length > 0" class="row">
          <span class="small muted">{{ t().authenticatesWith }}</span>
          <span v-for="h in store.authHints" :key="h.scheme" class="chip mono" :title="h.handler">{{ h.scheme }} · {{ h.kind }}</span>
        </div>

        <div v-if="cookieHint" class="notice">
          <Icon name="globe" :size="16" />
          <div class="notice-body">
            <strong>{{ t().cookieAuth }}</strong>
            <p>{{ t().cookieAuthText }}</p>
          </div>
        </div>
        <div v-for="h in browserManaged" :key="h.scheme" class="notice">
          <Icon name="globe" :size="16" />
          <div class="notice-body">
            <strong>{{ h.scheme }}</strong>
            <p>{{ h.kind === "negotiate" ? t().negotiateText : t().certificateText }}</p>
          </div>
        </div>

        <div class="scheme" :class="{ active: credentials.some((c) => c.kind === 'bearer' || c.kind === 'basic') }">
          <div class="scheme-head">
            <Icon name="key" :size="16" />
            <strong>{{ t().authorizationHeader }}</strong>
            <span class="grow" />
            <div class="tabs">
              <button type="button" :class="{ on: form.authorization === 'bearer' }" @click="form.authorization = 'bearer'">Bearer</button>
              <button type="button" :class="{ on: form.authorization === 'basic' }" @click="form.authorization = 'basic'">Basic</button>
            </div>
          </div>
          <label v-if="form.authorization === 'bearer'" class="label">
            {{ t().token }}
            <div class="control-row">
              <input ref="first" v-model="form.token" class="input mono" :type="showSecret ? 'text' : 'password'" autocomplete="off" spellcheck="false" :placeholder="t().tokenPlaceholder" />
              <button type="button" class="btn icon" :title="showSecret ? t().hide : t().show" @click="showSecret = !showSecret"><Icon :name="showSecret ? 'eye-off' : 'eye'" :size="15" /></button>
            </div>
          </label>
          <div v-else class="scheme-grid">
            <label class="label">{{ t().userName }}<input ref="first" v-model="form.username" class="input" autocomplete="off" spellcheck="false" /></label>
            <label class="label">
              {{ t().password }}
              <div class="control-row">
                <input v-model="form.password" class="input" :type="showSecret ? 'text' : 'password'" autocomplete="off" />
                <button type="button" class="btn icon" :title="showSecret ? t().hide : t().show" @click="showSecret = !showSecret"><Icon :name="showSecret ? 'eye-off' : 'eye'" :size="15" /></button>
              </div>
            </label>
          </div>
        </div>

        <div class="scheme" :class="{ active: credentials.some((c) => c.kind === 'api-key') }">
          <div class="scheme-head">
            <Icon name="key" :size="16" />
            <strong>{{ t().apiKeys }}</strong>
            <span class="faint small">{{ t().apiKeysHint }}</span>
            <span class="grow" />
            <button type="button" class="btn small" @click="addApiKey"><Icon name="plus" :size="13" />{{ t().addKey }}</button>
          </div>
          <div v-for="(k, i) in form.apiKeys" :key="i" class="scheme-grid">
            <label class="label">{{ t().header }}<input v-model="k.header" class="input mono" spellcheck="false" placeholder="X-API-Key" /></label>
            <label class="label">
              {{ t().value }}
              <div class="control-row">
                <input v-model="k.value" class="input mono" :type="showSecret ? 'text' : 'password'" autocomplete="off" spellcheck="false" />
                <button type="button" class="btn icon ghost" :title="t().remove" @click="form.apiKeys.splice(i, 1)"><Icon name="trash" :size="14" /></button>
              </div>
            </label>
          </div>
          <span v-if="form.apiKeys.length === 0" class="faint small">{{ t().noApiKey }}</span>
        </div>

        <div class="scheme" :class="{ active: credentials.some((c) => c.kind === 'header') }">
          <div class="scheme-head">
            <Icon name="layers" :size="16" />
            <strong>{{ t().otherHeaders }}</strong>
            <span class="faint small">{{ t().otherHeadersHint }}</span>
            <span class="grow" />
            <button type="button" class="btn small" @click="form.headers.push({ name: '', value: '' })"><Icon name="plus" :size="13" />{{ t().addHeader }}</button>
          </div>
          <div v-for="(h, i) in form.headers" :key="i" class="scheme-grid">
            <label class="label">{{ t().name }}<input v-model="h.name" class="input mono" spellcheck="false" placeholder="X-Tenant" /></label>
            <label class="label">
              {{ t().value }}
              <div class="control-row">
                <input v-model="h.value" class="input mono" :type="showSecret ? 'text' : 'password'" autocomplete="off" spellcheck="false" />
                <button type="button" class="btn icon ghost" :title="t().remove" @click="form.headers.splice(i, 1)"><Icon name="trash" :size="14" /></button>
              </div>
            </label>
          </div>
          <span v-if="form.headers.length === 0" class="faint small">{{ t().noOtherHeader }}</span>
        </div>

        <div v-for="p in problems" :key="describeCredential(p.c)" class="notice bad">
          <Icon name="alert" :size="16" />
          <div class="notice-body"><strong>{{ describeCredential(p.c) }}</strong><p>{{ p.problem }}</p></div>
        </div>
      </div>

      <div class="dialog-foot">
        <button v-if="store.credentials.length > 0" type="button" class="btn danger" @click="forget"><Icon name="unlock" :size="14" />{{ t().signOut }}</button>
        <span class="grow" />
        <button type="button" class="btn" @click="emit('close')">{{ t().cancel }}</button>
        <button type="submit" class="btn primary" :disabled="problems.length > 0"><Icon name="lock" :size="14" />{{ t().authorizeSubmit }}</button>
      </div>
      </form>
    </div>
  </div>
</template>
