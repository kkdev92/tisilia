<script setup lang="ts">
// The decoded public value (what the generated client returns), with each value's kind: int64 and decimal keep their digits, dates their
// offset and ticks. Collapsible, long lists in pages; masked leaves stay masked.
import { computed, ref } from "vue";
import type { DisplayNode } from "../explorer.js";
import { t } from "../i18n.js";
import Icon from "./Icon.vue";

defineOptions({ name: "DisplayTree" });

const props = defineProps<{ node: DisplayNode; depth?: number }>();
const depth = computed(() => props.depth ?? 0);
const open = ref(depth.value < 3);
const kids = computed(() => props.node.children ?? []);
const page = ref(100);
const shown = computed(() => kids.value.slice(0, page.value));
const summary = computed(() => (props.node.kind === "array" ? `[${kids.value.length}]` : props.node.kind === "map" ? `map(${kids.value.length})` : `{ ${kids.value.length} }`));
const valueClass = computed(() => {
  switch (props.node.kind) {
    case "string":
    case "date-only":
    case "time-only":
    case "datetime-utc":
    case "datetime-unspecified":
    case "datetime-local-wire":
    case "datetime-offset":
    case "duration":
    case "bytes":
      return "t-string";
    case "number":
    case "int64":
    case "decimal":
      return "t-number";
    case "boolean":
    case "null":
    case "absent":
      return "t-literal";
    default:
      return "";
  }
});
</script>

<template>
  <div>
    <div class="tree-row">
      <button v-if="node.children" type="button" class="tree-toggle" :aria-expanded="open" @click="open = !open"><Icon name="chevron" :size="12" class="chev" :class="{ open }" /></button>
      <span v-else class="tree-pad" />
      <span v-if="node.label" class="t-key">{{ node.label }}</span><span v-if="node.label" class="t-punct">:</span>
      <span v-if="node.children" class="tree-summary">{{ summary }}</span>
      <span v-else :class="[valueClass, node.text === '•••' ? 't-masked' : '']">{{ node.kind === "string" && node.text !== "•••" ? JSON.stringify(node.text) : node.text }}</span>
      <span class="tree-type">{{ node.kind }}</span>
    </div>
    <div v-if="node.children && open" class="tree-children">
      <DisplayTree v-for="(c, i) in shown" :key="i" :node="c" :depth="depth + 1" />
      <button v-if="kids.length > shown.length" type="button" class="tree-more" @click="page += 200">{{ t().moreItems(kids.length - shown.length) }}</button>
    </div>
  </div>
</template>
