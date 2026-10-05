<script setup lang="ts">
// A body's shape as the wire carries it: members with their types, required/optional, null, formats, enum members, union variants —
// and what the API's documentation says about each member. Recursive types show a back-reference instead of unfolding
// forever.
import { computed, ref } from "vue";
import { writeJson } from "@kkdev92/tisilia-runtime";
import { docOf, isDeprecated } from "../docs.js";
import type { InputNode } from "../forms.js";
import { t } from "../i18n.js";
import { store } from "../state.js";
import Icon from "./Icon.vue";
import RichText from "./RichText.js";

defineOptions({ name: "TypeTree" });

const props = defineProps<{ node: InputNode; name?: string; required?: boolean; doc?: string | undefined; ancestors?: readonly InputNode[]; depth?: number }>();
const depth = computed(() => props.depth ?? 0);
const open = ref(depth.value < 2);
const ancestors = computed(() => [...(props.ancestors ?? []), props.node]);
const recursive = computed(() => (props.ancestors ?? []).includes(props.node));

const core = computed<InputNode>(() => (props.node.kind === "nullable" ? props.node.inner : props.node));
const nullable = computed(() => props.node.kind === "nullable");
const label = computed(() => {
  const n = core.value;
  switch (n.kind) {
    case "scalar":
      return n.typeLabel;
    case "object":
    case "array":
    case "tagged":
      return n.typeLabel;
    case "literal":
      return writeJson(n.value);
    case "choice":
      return n.branches.map((b) => b.token).join(" | ");
    case "json":
      return "JSON";
    default:
      return n.kind;
  }
});
const format = computed(() => {
  const n = core.value;
  if (n.kind !== "scalar") {
    return "";
  }
  if (n.token === "string" && (n.widget === "integer" || n.widget === "decimal" || n.widget === "float")) {
    return t().numberAsString(n.widget);
  }
  return n.hint === "text" || n.hint === "one member" ? "" : t().hint(n.hint);
});
const children = computed(() => {
  const n = core.value;
  if (recursive.value) {
    return [];
  }
  switch (n.kind) {
    case "object": {
      const members = docOf(store.model!.document, n.typeId)?.members;
      return [
        ...n.properties.map((p) => ({ name: p.name, required: p.required, node: p.node, doc: members?.get(p.name) })),
        ...(n.additional ? [{ name: `[${n.additional.key.typeLabel}]`, required: false, node: n.additional.value, doc: undefined }] : []),
      ];
    }
    case "array":
      return [{ name: "[ ]", required: true, node: n.element, doc: undefined }];
    case "tagged":
      return n.variants.map((v) => ({ name: v.label, required: false, node: v.node, doc: undefined }));
    case "choice":
      return n.branches.map((b) => ({ name: b.token, required: false, node: b.node, doc: undefined }));
    default:
      return [];
  }
});
// an enum's members with what the documentation says about each (only when it says something)
const choices = computed(() => {
  const n = core.value;
  if (n.kind !== "scalar" || n.choices === undefined) {
    return [];
  }
  const members = docOf(store.model!.document, n.typeId)?.members;
  return members === undefined || members.size === 0 ? [] : n.choices.map((c) => ({ label: c.label, doc: c.member === undefined ? undefined : members.get(c.member) }));
});
const expandable = computed(() => children.value.length > 0 || choices.value.length > 0);
</script>

<template>
  <div>
    <div class="type-row">
      <button v-if="expandable" type="button" class="tree-toggle" :aria-expanded="open" @click="open = !open"><Icon name="chevron" :size="12" class="chev" :class="{ open }" /></button>
      <span v-else class="tree-pad" />
      <span v-if="name" class="name" :class="{ deprecated: isDeprecated(doc) }">{{ name }}<span v-if="required" class="req" :title="t().requiredLower">*</span></span>
      <span class="type">{{ label }}{{ nullable ? " | null" : "" }}</span>
      <span v-if="recursive" class="chip">{{ t().recursive }}</span>
      <span v-if="core.kind === 'tagged'" class="chip mono">{{ t().discriminatedBy(core.discriminator) }}</span>
      <span v-if="core.kind === 'object' && core.closed" class="chip" :title="t().closedTitle">{{ t().closed }}</span>
      <span v-if="format" class="faint small">{{ format }}</span>
      <span v-if="core.kind === 'scalar' && core.choices && choices.length === 0" class="faint small">{{ core.choices.map((c) => c.label).join(" · ") }}</span>
      <RichText v-if="doc" class="type-doc" :text="doc" inline />
    </div>
    <div v-if="expandable && open" class="type-children">
      <TypeTree v-for="c in children" :key="c.name" :node="c.node" :name="c.name" :required="c.required" :doc="c.doc" :ancestors="ancestors" :depth="depth + 1" />
      <div v-for="c in choices" :key="c.label" class="type-row">
        <span class="tree-pad" />
        <span class="name" :class="{ deprecated: isDeprecated(c.doc) }">{{ c.label }}</span>
        <RichText v-if="c.doc" class="type-doc" :text="c.doc" inline />
      </div>
    </div>
  </div>
</template>
