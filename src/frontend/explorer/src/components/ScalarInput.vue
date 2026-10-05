<script setup lang="ts">
// One scalar: the control follows the wire grammar and the domain type (enum members, dates, GUIDs, numbers). It edits text only;
// the codecs decide what the text means — a number field keeps its digits, it is never rounded through `number`.
import { computed } from "vue";
import { newUuid, nowText, type ScalarNode } from "../forms.js";
import { t } from "../i18n.js";
import Icon from "./Icon.vue";

const props = defineProps<{ node: ScalarNode; text: string; invalid?: boolean; optional?: boolean; id?: string; placeholder?: string }>();
const emit = defineEmits<{ "update:text": [value: string] }>();

const set = (value: string): void => emit("update:text", value);
const choiceText = (c: NonNullable<ScalarNode["choices"]>[number]): string => (c.value.kind === "string" ? c.value.value : c.value.kind === "number" ? c.value.text : "");

// flags: names joined the way System.Text.Json writes them ("A, B"), or the OR of the values on a number wire
const flagSet = computed(() => {
  if (props.node.widget !== "flags") {
    return new Set<string>();
  }
  const text = props.text.trim();
  if (props.node.token === "number" && /^\d+$/.test(text)) {
    const value = BigInt(text);
    return new Set((props.node.choices ?? []).filter((c) => c.value.kind === "number" && BigInt(c.value.text) !== 0n && (value & BigInt(c.value.text)) === BigInt(c.value.text)).map(choiceText));
  }
  return new Set(text.split(",").map((s) => s.trim()).filter((s) => s.length > 0));
});

function toggleFlag(name: string, on: boolean): void {
  const next = new Set(flagSet.value);
  if (on) {
    next.add(name);
  } else {
    next.delete(name);
  }
  if (props.node.token === "number") {
    set(String([...next].reduce((acc, n) => acc | BigInt(n), 0n)));
  } else {
    set([...next].join(", "));
  }
}

const multiline = computed(() => props.node.widget === "text" && (props.text.includes("\n") || props.text.length > 80));
const timeWidget = computed(() => ["date", "time", "datetime-offset", "datetime-utc", "datetime"].includes(props.node.widget));
const numeric = computed(() => ["integer", "decimal", "float"].includes(props.node.widget));
</script>

<template>
  <div class="control-row">
    <select v-if="node.widget === 'enum'" :id="id" class="select mono" :class="{ invalid }" :value="text" @change="set(($event.target as HTMLSelectElement).value)">
      <option value="">{{ optional ? t().notSent : t().choose }}</option>
      <option v-for="c in node.choices ?? []" :key="c.label" :value="choiceText(c)">{{ c.label }}</option>
      <option v-if="text !== '' && !(node.choices ?? []).some((c) => choiceText(c) === text)" :value="text">{{ t().notAMember(text) }}</option>
    </select>
    <div v-else-if="node.widget === 'flags'" class="flags">
      <label v-for="c in node.choices ?? []" :key="c.label">
        <input type="checkbox" :checked="flagSet.has(choiceText(c))" @change="toggleFlag(choiceText(c), ($event.target as HTMLInputElement).checked)" />
        {{ c.label }}
      </label>
    </div>
    <select v-else-if="node.widget === 'boolean'" :id="id" class="select mono" :class="{ invalid }" :value="text" @change="set(($event.target as HTMLSelectElement).value)">
      <option value="">{{ optional ? t().notSent : t().choose }}</option>
      <option value="true">true</option>
      <option value="false">false</option>
    </select>
    <textarea v-else-if="multiline" :id="id" class="textarea" :class="{ invalid }" :value="text" :placeholder="placeholder ?? t().hint(node.hint)" spellcheck="false" @input="set(($event.target as HTMLTextAreaElement).value)" />
    <input
      v-else
      :id="id"
      class="input mono"
      :class="{ invalid }"
      :value="text"
      :placeholder="placeholder ?? t().hint(node.hint)"
      :inputmode="numeric ? 'decimal' : undefined"
      :maxlength="node.widget === 'char' ? 2 : node.maxLength"
      spellcheck="false"
      autocomplete="off"
      @input="set(($event.target as HTMLInputElement).value)"
    />
    <button v-if="node.widget === 'guid'" type="button" class="btn small icon ghost" :title="t().newGuid" @click="set(newUuid())"><Icon name="wand" :size="14" /></button>
    <button v-if="timeWidget" type="button" class="btn small ghost" :title="t().nowTitle" @click="set(nowText(node.widget))"><Icon name="clock" :size="14" />{{ t().now }}</button>
  </div>
</template>
