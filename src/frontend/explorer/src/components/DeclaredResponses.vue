<script setup lang="ts">
// The responses the contract declares for an operation, one compact row each — status, the case id the client returns, the body
// type — opening to an example value or the schema. After a call, the row it came back as is marked.
import { computed, reactive } from "vue";
import type { ContractOperation } from "@kkdev92/tisilia-runtime";
import { docOf } from "../docs.js";
import { responseOf } from "../explorer.js";
import { exampleOf, prettyJson, sanitize } from "../forms.js";
import { t } from "../i18n.js";
import { store, type Run } from "../state.js";
import CodeBlock from "./CodeBlock.vue";
import Icon from "./Icon.vue";
import RichText from "./RichText.js";
import TypeTree from "./TypeTree.vue";

const props = defineProps<{ op: ContractOperation; run: Run | undefined }>();
const opened = reactive<Record<string, boolean>>({});
const views = reactive<Record<string, "example" | "schema">>({});

const responses = computed(() =>
  props.op.responses.map((r) => ({
    id: r.id,
    status: r.status,
    mediaType: r.body.kind === "none" ? undefined : r.body.mediaType,
    type: r.body.kind === "none" ? undefined : r.body.kind === "binary" ? "BufferedFile" : store.schema!.typeLabel(r.body.use.typeId),
    node: r.body.kind === "none" || r.body.kind === "binary" ? undefined : store.schema!.response(r.body.use),
    hydration: r.hydration,
    doc: docOf(store.model!.document, r.id)?.summary,
    typeDoc: r.body.kind === "none" || r.body.kind === "binary" ? undefined : docOf(store.model!.document, r.body.use.typeId)?.summary,
  })),
);
// an example is made when its row is first opened, and kept (it does not change under the reader); a plain cache, not state
const examples = new Map<string, string>();
function exampleText(r: (typeof responses.value)[number]): string {
  let text = examples.get(r.id);
  if (text === undefined) {
    text = r.node === undefined ? "" : prettyJson(sanitize(exampleOf(r.node)));
    examples.set(r.id, text);
  }
  return text;
}

// the case it decoded as; a failure that still has a status (a body that did not decode) marks the rows with that status
const received = computed(() => (props.run === undefined ? undefined : responseOf(props.run.execution)));
const isReceived = (r: { id: string; status: number }): boolean =>
  received.value !== undefined && (received.value.caseId !== undefined ? received.value.caseId === r.id : received.value.status === r.status);
const statusClass = (status: number): string => "s" + String(status)[0];
</script>

<template>
  <div class="outcomes">
    <h4 class="sub-head">{{ t().declaredResponses }}</h4>
    <div v-for="r in responses" :key="r.id" class="outcome" :class="{ received: isReceived(r), opened: opened[r.id] }">
      <component :is="r.node ? 'button' : 'div'" :type="r.node ? 'button' : undefined" class="outcome-row" :aria-expanded="r.node ? opened[r.id] === true : undefined" @click="r.node && (opened[r.id] = !opened[r.id])">
        <span class="status small" :class="statusClass(r.status)">{{ r.status }}</span>
        <code class="outcome-case" :title="t().caseIdTitle">{{ r.id }}</code>
        <span v-if="r.type" class="outcome-type">{{ r.type }}</span>
        <span v-else class="outcome-type faint">{{ t().noBodyShort }}</span>
        <span v-if="r.hydration === 'server-only'" class="chip warn" :title="t().ssrOnlyTitle">{{ t().ssrOnly }}</span>
        <span class="grow" />
        <span v-if="isReceived(r)" class="chip accent">{{ t().lastResponse }}</span>
        <Icon v-if="r.node" class="chev" :class="{ open: opened[r.id] }" name="chevron" :size="14" />
        <RichText v-if="r.doc" class="outcome-doc" :text="r.doc" inline :links="!r.node" />
      </component>
      <div v-if="r.node && opened[r.id]" class="outcome-body">
        <RichText v-if="r.typeDoc" class="body-doc" :text="r.typeDoc" inline />
        <div class="row">
          <div class="tabs" role="tablist" :aria-label="t().declaredBody">
            <button type="button" role="tab" :aria-selected="(views[r.id] ?? 'example') === 'example'" :class="{ on: (views[r.id] ?? 'example') === 'example' }" @click="views[r.id] = 'example'">{{ t().example }}</button>
            <button type="button" role="tab" :aria-selected="views[r.id] === 'schema'" :class="{ on: views[r.id] === 'schema' }" @click="views[r.id] = 'schema'">{{ t().schema }}</button>
          </div>
          <code class="faint small">{{ r.mediaType }}</code>
        </div>
        <div v-if="views[r.id] === 'schema'" class="type-tree"><TypeTree :node="r.node" /></div>
        <CodeBlock v-else :code="exampleText(r)" language="json" wrap />
      </div>
    </div>
  </div>
</template>
