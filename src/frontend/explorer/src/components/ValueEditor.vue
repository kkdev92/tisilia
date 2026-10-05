<script setup lang="ts">
// Recursive form for one value of a request body. It edits the JSON AST the codecs read; every change is a new value
// emitted upwards, and the codecs' errors come back by JSON pointer (`errors`) to show next to the field they name.
import { computed, ref, watch } from "vue";
import { writeJson } from "@kkdev92/tisilia-runtime";
import { exampleOf, findEntry, parseFormJson, prettyJson, sanitize, scalarText, scalarValue, toFormJson, variantIndex, type FormJson, type InputNode, type ObjectNode, type ScalarNode } from "../forms.js";
import { t } from "../i18n.js";
import Icon from "./Icon.vue";
import ScalarInput from "./ScalarInput.vue";

defineOptions({ name: "ValueEditor" });

const props = defineProps<{ node: InputNode; value: FormJson | undefined; path: string; errors: ReadonlyMap<string, string>; depth?: number }>();
const emit = defineEmits<{ update: [value: FormJson | undefined] }>();
const depth = computed(() => props.depth ?? 0);
const error = computed(() => props.errors.get(props.path));

// ---------------------------------------------------------------- helpers

const inline = (node: InputNode): boolean => node.kind === "scalar" || node.kind === "boolean" || node.kind === "literal" || node.kind === "null" || (node.kind === "nullable" && inline(node.inner));

function typeOf(node: InputNode): string {
  switch (node.kind) {
    case "scalar":
      return node.typeLabel;
    case "object":
    case "array":
    case "tagged":
      return node.typeLabel;
    case "nullable":
      return typeOf(node.inner) + " | null";
    case "literal":
      return t().constant;
    case "choice":
      return node.branches.map((b) => b.token).join(" | ");
    default:
      return node.kind;
  }
}

function entries(value: FormJson | undefined): { name: string; value: FormJson }[] {
  return value?.kind === "object" ? value.entries : [];
}

// properties in the wire's order, extra entries (a dictionary's, extension data) after them
function setProperty(node: ObjectNode, name: string, next: FormJson | undefined): void {
  const current = entries(props.value);
  const declared = new Map(node.properties.map((p, i) => [p.name, i]));
  const map = new Map(current.map((e) => [e.name, e.value]));
  if (next === undefined) {
    map.delete(name);
  } else {
    map.set(name, next);
  }
  const ordered = [...map.entries()].map(([n, v]) => ({ name: n, value: v })).sort((a, b) => (declared.get(a.name) ?? 1e9) - (declared.get(b.name) ?? 1e9));
  emit("update", { kind: "object", entries: ordered });
}

const extra = computed(() => {
  if (props.node.kind !== "object") {
    return [];
  }
  const declared = new Set(props.node.properties.map((p) => p.name));
  return entries(props.value).map((e, index) => ({ ...e, index })).filter((e) => !declared.has(e.name));
});

function setExtraKey(index: number, key: string): void {
  const list = entries(props.value).map((e, i) => (i === index ? { name: key, value: e.value } : e));
  emit("update", { kind: "object", entries: list });
}

function setExtraValue(index: number, next: FormJson | undefined): void {
  const list = entries(props.value).flatMap((e, i) => (i === index ? (next === undefined ? [] : [{ name: e.name, value: next }]) : [e]));
  emit("update", { kind: "object", entries: list });
}

function addExtra(node: ObjectNode): void {
  if (node.additional === undefined) {
    return;
  }
  const keyExample = exampleOf(node.additional.key);
  let key = keyExample.kind === "string" ? (keyExample.value === "string" ? "key" : keyExample.value) : keyExample.kind === "number" ? keyExample.text : "key";
  const taken = new Set(entries(props.value).map((e) => e.name));
  for (let i = 2; taken.has(key); i++) {
    key = (keyExample.kind === "number" ? String(Number(keyExample.text) + i - 1) : (keyExample.kind === "string" && keyExample.value !== "string" ? keyExample.value : "key") + i);
  }
  emit("update", { kind: "object", entries: [...entries(props.value), { name: key, value: exampleOf(node.additional.value) }] });
}

const items = computed(() => (props.value?.kind === "array" ? props.value.items : []));

function setItem(index: number, next: FormJson | undefined): void {
  const list = items.value.flatMap((v, i) => (i === index ? (next === undefined ? [] : [next]) : [v]));
  emit("update", { kind: "array", items: list });
}

function moveItem(index: number, delta: number): void {
  const list = [...items.value];
  const [item] = list.splice(index, 1);
  list.splice(index + delta, 0, item!);
  emit("update", { kind: "array", items: list });
}

const variant = computed(() => (props.node.kind === "tagged" ? variantIndex(props.node, props.value) : 0));

function chooseVariant(index: number): void {
  if (props.node.kind === "tagged") {
    emit("update", exampleOf(props.node.variants[index]!.node));
  }
}

const isNull = computed(() => props.value?.kind === "null");

function setNull(on: boolean): void {
  if (props.node.kind === "nullable") {
    emit("update", on ? { kind: "null" } : exampleOf(props.node.inner));
  }
}

const branch = computed(() => {
  if (props.node.kind !== "choice") {
    return 0;
  }
  const kind = props.value?.kind;
  const index = props.node.branches.findIndex((b) => b.token === kind);
  return index < 0 ? 0 : index;
});

// free JSON (JsonElement, JsonNode): edited as text, kept as the last value that parsed
const jsonText = ref("");
const jsonError = ref<string>();
watch(
  () => props.value,
  (v) => {
    if (props.node.kind !== "json") {
      return;
    }
    // the user's text stays while it means the same value (their formatting, a key order); a new value from outside replaces it
    const parsed = parseFormJson(jsonText.value);
    const current = "value" in parsed ? writeJson(sanitize(parsed.value)) : undefined;
    const next = v === undefined ? undefined : writeJson(sanitize(v));
    if (current !== next) {
      jsonText.value = v === undefined ? "" : prettyJson(sanitize(v));
      jsonError.value = undefined;
    }
  },
  { immediate: true },
);

function editJson(text: string): void {
  jsonText.value = text;
  const parsed = parseFormJson(text);
  if ("error" in parsed) {
    jsonError.value = parsed.error;
    return;
  }
  jsonError.value = undefined;
  emit("update", parsed.value);
}

const literalText = computed(() => (props.node.kind === "literal" ? writeJson(props.node.value) : ""));
watch(
  () => [props.node, props.value] as const,
  () => {
    // a literal (a variant's discriminator) always holds its value
    if (props.node.kind === "literal" && (props.value === undefined || writeJson(sanitize(props.value)) !== literalText.value)) {
      emit("update", toFormJson(props.node.value));
    }
  },
  { immediate: true },
);
</script>

<template>
  <!-- scalar -->
  <div v-if="node.kind === 'scalar'" class="field-control">
    <ScalarInput :node="node" :text="scalarText(value)" :invalid="error !== undefined" @update:text="(t) => emit('update', scalarValue(node as ScalarNode, t))" />
    <span v-if="error" class="field-error"><Icon name="alert" :size="13" />{{ error }}</span>
  </div>

  <!-- boolean -->
  <div v-else-if="node.kind === 'boolean'" class="field-control">
    <label class="switch"><input type="checkbox" :checked="value?.kind === 'boolean' && value.value" @change="emit('update', { kind: 'boolean', value: ($event.target as HTMLInputElement).checked })" />{{ value?.kind === "boolean" && value.value ? "true" : "false" }}</label>
    <span v-if="error" class="field-error"><Icon name="alert" :size="13" />{{ error }}</span>
  </div>

  <!-- constant (a discriminator) -->
  <div v-else-if="node.kind === 'literal'" class="field-control">
    <div class="row"><span class="chip mono accent">{{ literalText }}</span><span class="faint small">{{ t().fixed }}</span></div>
  </div>

  <div v-else-if="node.kind === 'null'" class="field-control"><span class="chip mono">null</span></div>

  <!-- nullable -->
  <div v-else-if="node.kind === 'nullable'" class="field-control">
    <div v-if="inline(node.inner)" class="control-row">
      <div v-if="isNull" class="input mono faint">null</div>
      <ValueEditor v-else class="grow" :node="node.inner" :value="value" :path="path" :errors="errors" :depth="depth" @update="(v) => emit('update', v)" />
      <label class="switch small" :title="t().sendJsonNull"><input type="checkbox" :checked="isNull" @change="setNull(($event.target as HTMLInputElement).checked)" />null</label>
    </div>
    <template v-else>
      <label class="switch small"><input type="checkbox" :checked="isNull" @change="setNull(($event.target as HTMLInputElement).checked)" />null</label>
      <ValueEditor v-if="!isNull" :node="node.inner" :value="value" :path="path" :errors="errors" :depth="depth" @update="(v) => emit('update', v)" />
    </template>
    <span v-if="error && isNull" class="field-error"><Icon name="alert" :size="13" />{{ error }}</span>
  </div>

  <!-- object (and dictionary) -->
  <div v-else-if="node.kind === 'object'" :class="depth > 0 ? 'nest' : 'fields'">
    <span v-if="error" class="field-error"><Icon name="alert" :size="13" />{{ error }}</span>
    <template v-for="p in node.properties" :key="p.name">
      <div v-if="inline(p.node)" class="field" :class="{ absent: !p.required && !findEntry(value, p.name) }">
        <div class="field-label">
          <span class="field-name">{{ p.name }}<span v-if="p.required" class="req" :title="t().requiredLower">*</span></span>
          <span class="field-type">{{ typeOf(p.node) }}</span>
        </div>
        <div class="field-control">
          <div v-if="findEntry(value, p.name) || p.required" class="control-row">
            <ValueEditor class="grow" :node="p.node" :value="findEntry(value, p.name)?.value" :path="path + '/' + p.name" :errors="errors" :depth="depth + 1" @update="(v) => setProperty(node as ObjectNode, p.name, v)" />
            <button v-if="!p.required" type="button" class="btn small icon ghost" :title="t().omitProperty" @click="setProperty(node as ObjectNode, p.name, undefined)"><Icon name="x" :size="14" /></button>
          </div>
          <button v-else type="button" class="btn small" @click="setProperty(node as ObjectNode, p.name, exampleOf(p.node))"><Icon name="plus" :size="13" />{{ t().addLower }}</button>
          <span v-if="!findEntry(value, p.name) && p.required && errors.get(path + '/' + p.name)" class="field-error"><Icon name="alert" :size="13" />{{ errors.get(path + "/" + p.name) }}</span>
        </div>
      </div>
      <div v-else class="fields">
        <div class="nest-head">
          <span class="field-name">{{ p.name }}<span v-if="p.required" class="req" :title="t().requiredLower">*</span></span>
          <span class="chip mono">{{ typeOf(p.node) }}</span>
          <span class="grow" />
          <button v-if="!p.required && findEntry(value, p.name)" type="button" class="btn small ghost" :title="t().omitProperty" @click="setProperty(node as ObjectNode, p.name, undefined)"><Icon name="x" :size="13" />{{ t().removeLower }}</button>
          <button v-if="!findEntry(value, p.name)" type="button" class="btn small" @click="setProperty(node as ObjectNode, p.name, exampleOf(p.node))"><Icon name="plus" :size="13" />{{ t().addLower }}</button>
        </div>
        <ValueEditor v-if="findEntry(value, p.name)" :node="p.node" :value="findEntry(value, p.name)!.value" :path="path + '/' + p.name" :errors="errors" :depth="depth + 1" @update="(v) => setProperty(node as ObjectNode, p.name, v)" />
        <span v-else-if="p.required && errors.get(path + '/' + p.name)" class="field-error"><Icon name="alert" :size="13" />{{ errors.get(path + "/" + p.name) }}</span>
      </div>
    </template>
    <template v-if="node.additional">
      <div v-for="e in extra" :key="e.index" class="item map">
        <ScalarInput :node="node.additional.key" :text="e.name" :placeholder="t().keyPlaceholder" @update:text="(k) => setExtraKey(e.index, k)" />
        <ValueEditor :node="node.additional.value" :value="e.value" :path="path + '/' + e.name" :errors="errors" :depth="depth + 1" @update="(v) => setExtraValue(e.index, v)" />
        <button type="button" class="btn small icon ghost" :title="t().removeEntry" @click="setExtraValue(e.index, undefined)"><Icon name="trash" :size="13" /></button>
      </div>
      <div class="row">
        <button type="button" class="btn small" @click="addExtra(node)"><Icon name="plus" :size="13" />{{ node.properties.length === 0 ? t().addEntry : t().addExtraMember }}</button>
        <span v-if="node.properties.length === 0" class="faint small">{{ t().entryCount(extra.length, node.additional.key.typeLabel) }}</span>
      </div>
    </template>
    <span v-if="node.properties.length === 0 && !node.additional" class="faint small">{{ t().noMembers }}</span>
  </div>

  <!-- array -->
  <div v-else-if="node.kind === 'array'" class="nest">
    <span v-if="error" class="field-error"><Icon name="alert" :size="13" />{{ error }}</span>
    <div v-for="(item, i) in items" :key="i" class="item">
      <span class="item-index">{{ i }}</span>
      <ValueEditor :node="node.element" :value="item" :path="path + '/' + i" :errors="errors" :depth="depth + 1" @update="(v) => setItem(i, v)" />
      <div class="row">
        <button type="button" class="btn small icon ghost" :title="t().moveUp" :disabled="i === 0" @click="moveItem(i, -1)"><Icon name="chevron-down" :size="13" class="flip" /></button>
        <button type="button" class="btn small icon ghost" :title="t().removeItem" @click="setItem(i, undefined)"><Icon name="trash" :size="13" /></button>
      </div>
    </div>
    <div class="row">
      <button type="button" class="btn small" @click="emit('update', { kind: 'array', items: [...items, exampleOf(node.element)] })"><Icon name="plus" :size="13" />{{ t().addItem }}</button>
      <span class="faint small">{{ t().itemCount(items.length) }}</span>
    </div>
  </div>

  <!-- tagged union -->
  <div v-else-if="node.kind === 'tagged'" class="nest">
    <div class="nest-head">
      <span class="faint small">{{ node.discriminator }} =</span>
      <select class="select mono" :value="variant" @change="chooseVariant(Number(($event.target as HTMLSelectElement).value))">
        <option v-for="(v, i) in node.variants" :key="i" :value="i">{{ v.label }}</option>
      </select>
    </div>
    <span v-if="error" class="field-error"><Icon name="alert" :size="13" />{{ error }}</span>
    <ValueEditor v-if="node.variants[variant]" :node="node.variants[variant]!.node" :value="value" :path="path" :errors="new Map([...errors].filter(([k]) => k !== path))" :depth="depth" @update="(v) => emit('update', v)" />
  </div>

  <!-- token choice -->
  <div v-else-if="node.kind === 'choice'" class="field-control">
    <div class="tabs">
      <button v-for="(b, i) in node.branches" :key="b.token" type="button" :class="{ on: i === branch }" @click="emit('update', exampleOf(b.node))">{{ b.token }}</button>
    </div>
    <ValueEditor v-if="node.branches[branch]" :node="node.branches[branch]!.node" :value="value" :path="path" :errors="errors" :depth="depth" @update="(v) => emit('update', v)" />
  </div>

  <!-- free JSON -->
  <div v-else class="field-control">
    <textarea class="textarea" :class="{ invalid: jsonError !== undefined || error !== undefined }" :value="jsonText" spellcheck="false" :placeholder="t().anyJson" @input="editJson(($event.target as HTMLTextAreaElement).value)" />
    <span v-if="jsonError" class="field-error"><Icon name="alert" :size="13" />{{ jsonError }}</span>
    <span v-else-if="error" class="field-error"><Icon name="alert" :size="13" />{{ error }}</span>
  </div>
</template>
