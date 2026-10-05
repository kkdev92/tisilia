<script setup lang="ts">
// "Schemas" at the end of the page: every named model of the contract (objects, enums, unions, maps, brands) with its CLR identity,
// each opening to its shape on the wire it travels on. A model's tree is built only when it is opened.
import { computed, reactive } from "vue";
import type { ContractTypeUse } from "@kkdev92/tisilia-runtime";
import { docOf, isDeprecated } from "../docs.js";
import type { InputNode } from "../forms.js";
import { t } from "../i18n.js";
import { store } from "../state.js";
import Icon from "./Icon.vue";
import RichText from "./RichText.js";
import TypeTree from "./TypeTree.vue";

const openModels = reactive<Record<string, boolean>>({});

const models = computed(() =>
  store
    .model!.document.types.filter((t) => t.shape.kind === "object" || t.shape.kind === "enum" || t.shape.kind === "union" || t.shape.kind === "map" || t.shape.kind === "brand")
    .slice()
    .sort((a, b) => a.tsName.localeCompare(b.tsName) || a.id.localeCompare(b.id)),
);

/** The model's shape on the wire it travels on: the response wire when it has one, the request wire otherwise. */
function nodeOf(typeId: string): InputNode | undefined {
  const codecs = store.model!.document.codecs.filter((c) => c.typeId === typeId && !c.id.endsWith(".nullable"));
  const codec = codecs.find((c) => c.capabilities.response !== undefined) ?? codecs[0];
  if (codec === undefined) {
    return undefined;
  }
  const use: ContractTypeUse = { typeId, codecId: codec.id, semanticNullable: false };
  return codec.capabilities.response !== undefined ? store.schema!.response(use) : store.schema!.body(use);
}
</script>

<template>
  <section class="group schemas" :class="{ open: store.schemasOpen }">
    <h2 class="group-head">
      <button type="button" class="group-toggle" :aria-expanded="store.schemasOpen" @click="store.schemasOpen = !store.schemasOpen">
        <Icon class="chev" :class="{ open: store.schemasOpen }" name="chevron" :size="16" />
        <span class="group-name">{{ t().schemas }}</span>
        <span class="group-count">{{ models.length }}</span>
      </button>
    </h2>
    <div v-if="store.schemasOpen" class="schema-list">
      <div v-for="m in models" :key="m.id" class="schema" :class="{ opened: openModels[m.id] === true }">
        <button type="button" class="schema-row" :aria-expanded="openModels[m.id] === true" @click="openModels[m.id] = !openModels[m.id]">
          <Icon class="chev" :class="{ open: openModels[m.id] === true }" name="chevron" :size="14" />
          <span class="schema-name" :class="{ deprecated: isDeprecated(docOf(store.model!.document, m.id)?.text) }">{{ m.tsName }}</span>
          <span class="chip">{{ m.shape.kind }}</span>
          <span v-if="isDeprecated(docOf(store.model!.document, m.id)?.text)" class="chip warn">{{ t().deprecated }}</span>
          <RichText v-if="docOf(store.model!.document, m.id)?.summary" class="schema-doc" :text="docOf(store.model!.document, m.id)!.summary" :title="docOf(store.model!.document, m.id)!.summary" inline :links="false" />
          <span class="schema-clr" :title="m.clrIdentity">{{ m.clrIdentity }}</span>
        </button>
        <div v-if="openModels[m.id] === true" class="schema-body type-tree">
          <RichText v-if="docOf(store.model!.document, m.id)?.text" class="schema-text" :text="docOf(store.model!.document, m.id)!.text" />
          <TypeTree v-if="nodeOf(m.id)" :node="nodeOf(m.id)!" />
          <p v-else class="col-note">{{ t().noWire }}</p>
        </div>
      </div>
    </div>
  </section>
</template>
