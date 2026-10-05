<script setup lang="ts">
// Code and JSON text, coloured as text nodes (never HTML), with a copy button. Copying revealed values asks first and names the
// scope: the clipboard is outside this page's redaction. A long text shows its first lines and the rest on request
// (large responses expand lazily); copy always takes the whole text.
import { computed, ref, watch } from "vue";
import { copyToClipboard } from "../explorer.js";
import { highlightJson, type Token } from "../highlight.js";
import { t } from "../i18n.js";
import { notify } from "../state.js";
import Icon from "./Icon.vue";

const props = defineProps<{ code: string; language?: "json" | "ts" | "shell" | "text"; wrap?: boolean; revealed?: boolean; lines?: number }>();
const step = 5000;
const limit = ref(props.lines ?? Number.POSITIVE_INFINITY);
watch(
  () => props.code,
  () => (limit.value = props.lines ?? Number.POSITIVE_INFINITY),
);

/** Where the first `count` lines end, or -1 when the text has no more lines than that. */
function endOfLines(text: string, count: number): number {
  let at = -1;
  for (let n = 0; n < count; n++) {
    at = text.indexOf("\n", at + 1);
    if (at < 0) {
      return -1;
    }
  }
  return at;
}

const cut = computed(() => (Number.isFinite(limit.value) ? endOfLines(props.code, limit.value) : -1));
const visible = computed(() => (cut.value < 0 ? props.code : props.code.slice(0, cut.value)));
const hiddenLines = computed(() => {
  if (cut.value < 0) {
    return 0;
  }
  let n = 0;
  for (let at = cut.value; at >= 0; at = props.code.indexOf("\n", at + 1)) {
    n++;
  }
  return n;
});
const moreLabel = computed(() =>
  hiddenLines.value > step ? t().showMoreLines(step) : t().showRemainingLines(hiddenLines.value),
);

const codePattern = /(\/\/[^\n]*)|("(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`)|(\b-?\d[\d_]*(?:\.\d+)?n?\b)|(\b(?:import|from|const|let|await|async|new|return|if|else|true|false|null|undefined|curl)\b)|(\$[A-Z_]+|\$\{[A-Z_]+\})/g;

const tokens = computed<Token[]>(() => {
  const code = visible.value;
  if (props.language === "json") {
    return highlightJson(code);
  }
  if (props.language === "text" || props.language === undefined) {
    return [{ kind: "plain", text: code }];
  }
  const out: Token[] = [];
  let last = 0;
  for (const m of code.matchAll(codePattern)) {
    if (m.index! > last) {
      out.push({ kind: "plain", text: code.slice(last, m.index) });
    }
    const kind: Token["kind"] = m[1] !== undefined ? "punct" : m[2] !== undefined ? "string" : m[3] !== undefined ? "number" : m[4] !== undefined ? "literal" : "key";
    out.push({ kind, text: m[0] });
    last = m.index! + m[0].length;
  }
  if (last < code.length) {
    out.push({ kind: "plain", text: code.slice(last) });
  }
  return out;
});

async function copy(): Promise<void> {
  if (props.revealed && !window.confirm(t().confirmCopyRevealed)) {
    return;
  }
  const outcome = await copyToClipboard(props.code, (navigator as Partial<Navigator>).clipboard);
  if (outcome.copied) {
    notify(t().copied);
  } else {
    notify(t().notCopied(outcome.reason), props.code);
  }
}

</script>

<template>
  <div class="code-wrap">
    <pre class="code" :class="{ wrap }"><span v-for="(t, i) in tokens" :key="i" :class="'t-' + t.kind">{{ t.text }}</span></pre>
    <button v-if="hiddenLines > 0" type="button" class="code-more" @click="limit += step">{{ moreLabel }}</button>
    <button type="button" class="btn small copy" :title="t().copy" @click="copy"><Icon name="copy" :size="13" />{{ t().copyLower }}</button>
  </div>
</template>
