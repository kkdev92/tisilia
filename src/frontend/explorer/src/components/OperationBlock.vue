<script setup lang="ts">
// One operation: a row (method, path, summary, lock) that opens into the request on the left and the response on the right — side
// by side on wide screens, stacked on narrow ones. Inputs are editable at once (no extra step before trying), the request line shows
// what Execute will send as the values change, and the answer appears next to the inputs. Inputs are built by the same codecs and
// request encoder a generated client uses; raw mode sends the edited text as written.
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { writeJson, type ContractOperation } from "@kkdev92/tisilia-runtime";
import { credentialShapes } from "../auth.js";
import { docOf, isDeprecated } from "../docs.js";
import { copyToClipboard, documentationOf, redactHeaders } from "../explorer.js";
import { errorsByPath, exampleOf, parseFormJson, prettyJson, sanitize, scalarText, type FormJson } from "../forms.js";
import { t } from "../i18n.js";
import { argsOf, authorized, cancel, clearRun, draftOf, notify, previewOf, resetDraft, runs, send, store, switchEditor, toggle } from "../state.js";
import CodeBlock from "./CodeBlock.vue";
import DeclaredResponses from "./DeclaredResponses.vue";
import Icon from "./Icon.vue";
import LiveResponse from "./LiveResponse.vue";
import ScalarInput from "./ScalarInput.vue";
import TypeTree from "./TypeTree.vue";
import RichText from "./RichText.js";
import ValueEditor from "./ValueEditor.vue";

/** `tag`: the group the block is listed in — an operation with several tags is listed in each (its first tag owns the plain ids). */
const props = defineProps<{ op: ContractOperation; tag: string }>();
const root = ref<HTMLElement>();
const live = ref<HTMLElement>();

const domKey = computed(() => (props.tag === (props.op.tags[0] ?? "default") ? props.op.id : props.tag + "-" + props.op.id));
const method = computed(() => props.op.method.toLowerCase());
const open = computed(() => store.open[props.op.id] === true);
const running = computed(() => store.running[props.op.id] === true);
const run = computed(() => runs.get(props.op.id));
const docs = computed(() => documentationOf(store.model!.document, props.op.id));
// [Obsolete] on the endpoint: the path struck through and "deprecated" said in words beside it
const deprecated = computed(() => isDeprecated(docs.value?.description));
const summary = computed(() => store.summaries.find((s) => s.id === props.op.id));
const profileId = computed(() => store.model!.registry.operations.get(props.op.id)?.profileId);
const routeParts = computed(() => props.op.route.split(/(\{[^{}]*\})/).filter((p) => p.length > 0).map((p) => ({ text: p, param: p.startsWith("{") })));

const draft = computed(() => draftOf(props.op));
const built = computed(() => argsOf(props.op, draft.value));
// a value that is merely missing is pointed out once the user has tried to execute or touched the field; a wrong value at once
const missing = (message: string): boolean => message === "required" || message === "request body is required" || message.startsWith("missing-required");
const errors = computed(() =>
  built.value.errors.filter((e) => draft.value.attempted || !missing(e.message) || draft.value.touched[e.path] === true || (e.path.startsWith("/body") && draft.value.touched["/body"] === true)),
);
const bodyErrors = computed(() => errorsByPath(errors.value.map((e) => ({ path: e.path, message: t().fieldError(e) }))));
const paramError = (name: string): string | undefined => {
  const error = errors.value.find((e) => e.path === "/" + name);
  return error === undefined ? undefined : t().fieldError(error);
};

const locations = ["path", "query", "header"] as const;
const parameters = computed(() =>
  props.op.parameters.map((p) => {
    const binder = store.model?.document.binders.find((b) => b.id === p.binderId);
    const node = store.schema!.parameter(p.use, binder?.grammarId);
    const serverDefault = p.serverDefault === undefined ? undefined : writeJson(p.serverDefault);
    return {
      id: p.id,
      name: p.name,
      doc: docOf(store.model!.document, p.id),
      location: p.location,
      required: p.presence === "required",
      nullable: p.use.semanticNullable,
      repeated: binder?.cardinality === "repeated",
      // an enum's default is its integer (the domain value): shown as the member it names, like the choices
      serverDefault: node.choices?.find((c) => c.number !== undefined && c.number === serverDefault)?.label ?? serverDefault,
      node,
    };
  }),
);
const groups = computed(() => locations.map((location) => ({ location, params: parameters.value.filter((p) => p.location === location) })).filter((g) => g.params.length > 0));

const bodyNode = computed(() => (props.op.requestBody.kind === "json" ? store.schema!.body(props.op.requestBody.use) : undefined));
const bodyType = computed(() => (props.op.requestBody.kind === "json" ? store.schema!.typeLabel(props.op.requestBody.use.typeId) : ""));
const bodyDoc = computed(() => (props.op.requestBody.kind === "json" ? docOf(store.model!.document, props.op.requestBody.use.typeId) : undefined));
const typedInput = computed(() => summary.value?.hasRequestInput !== false);
const schemaView = ref(false);
const bodyTab = computed<"json" | "form" | "schema">(() => (schemaView.value ? "schema" : draft.value.editor === "form" ? "form" : "json"));
// "Format": the JSON text laid out again, when it parses and is not laid out already
const formatted = computed(() => {
  if (draft.value.editor !== "json") {
    return undefined;
  }
  const parsed = parseFormJson(draft.value.bodyText);
  if ("error" in parsed) {
    return undefined;
  }
  const text = prettyJson(sanitize(parsed.value));
  return text === draft.value.bodyText ? undefined : text;
});

function selectBodyTab(tab: "json" | "form" | "schema"): void {
  schemaView.value = tab === "schema";
  if (tab !== "schema") {
    // JSON that does not parse keeps the JSON editor open (the notice says why)
    switchEditor(draft.value, tab);
  }
}

// ---------------------------------------------------------------- the request Execute will send

const previewed = computed(() => previewOf(props.op, draft.value, built.value));
const previewState = computed<"ok" | "pending" | "invalid">(() => (previewed.value.prepared !== undefined ? "ok" : errors.value.length > 0 ? "invalid" : "pending"));
const previewHeaders = computed(() => redactHeaders(previewed.value.prepared?.headers ?? [], store.reveal ? "reveal" : "mask"));
const credentialRows = computed(() =>
  credentialShapes(store.credentials).map((c) => (c.kind === "api-key" ? c.header.toLowerCase() : c.kind === "header" ? c.name.toLowerCase() : "authorization")),
);

// ---------------------------------------------------------------- editing

function edited(path: string): void {
  draft.value.touched[path] = true;
  draft.value.edited = true;
}

function setParam(name: string, text: string): void {
  draft.value.parameters[name] = text;
  edited("/" + name);
}

function listOf(name: string): string[] {
  const text = draft.value.parameters[name] ?? "";
  return text.length === 0 ? [] : text.split("\n");
}

function setListItem(name: string, index: number, value: string | undefined): void {
  const list = listOf(name);
  if (value === undefined) {
    list.splice(index, 1);
  } else if (index >= list.length) {
    list.push(value);
  } else {
    list[index] = value;
  }
  draft.value.parameters[name] = list.join("\n");
  edited("/" + name);
}

// empty parameters get an example of their kind (a fresh GUID, today, the first enum member); filled ones are left alone
const someEmpty = computed(() => parameters.value.some((p) => (draft.value.parameters[p.name] ?? "").length === 0));
function fillExamples(): void {
  for (const p of parameters.value) {
    if ((draft.value.parameters[p.name] ?? "").length === 0) {
      draft.value.parameters[p.name] = scalarText(exampleOf(p.node));
      edited("/" + p.name);
    }
  }
}

function setBody(value: FormJson | undefined): void {
  draft.value.body = value;
  edited("/body");
}

function formatBody(): void {
  if (formatted.value !== undefined) {
    draft.value.bodyText = formatted.value;
    edited("/body");
  }
}

async function copy(text: string, copied: string): Promise<void> {
  const outcome = await copyToClipboard(text, (navigator as Partial<Navigator>).clipboard);
  notify(outcome.copied ? copied : t().notCopied(outcome.reason), outcome.copied ? undefined : text);
}

const link = computed(() => window.location.origin + window.location.pathname + window.location.search + "#/op/" + encodeURIComponent(props.op.id));

// ---------------------------------------------------------------- executing

const reducedMotion = (): boolean => window.matchMedia("(prefers-reduced-motion: reduce)").matches;

/** Execute: inputs that need attention get the focus; an answer out of sight is brought into view. */
async function execute(): Promise<void> {
  const outcome = await send(props.op);
  await nextTick();
  if (outcome === "invalid") {
    const field = root.value?.querySelector<HTMLElement>("input.invalid, select.invalid, textarea.invalid") ?? root.value?.querySelector<HTMLElement>(".field-error");
    field?.scrollIntoView({ block: "center", behavior: reducedMotion() ? "auto" : "smooth" });
    field?.focus({ preventScroll: true });
  } else if (outcome === "sent" && live.value !== undefined) {
    const head = live.value.querySelector<HTMLElement>(".result-status") ?? live.value;
    const box = head.getBoundingClientRect();
    if (box.top < 70 || box.bottom > window.innerHeight) {
      head.scrollIntoView({ block: "start", behavior: reducedMotion() ? "auto" : "smooth" });
    }
  }
}

function onKey(event: KeyboardEvent): void {
  if ((event.ctrlKey || event.metaKey) && event.key === "Enter") {
    event.preventDefault();
    void execute();
  }
}

// a deep link (#/op/<id>) or Enter in the search brings its block into view once it has rendered
async function focusIfLinked(): Promise<void> {
  if (store.focusId !== props.op.id) {
    return;
  }
  store.focusId = undefined;
  await nextTick();
  root.value?.scrollIntoView({ block: "start" });
}
onMounted(focusIfLinked);
watch(() => store.focusId, focusIfLinked);
</script>

<template>
  <article :id="'operation-' + domKey" ref="root" class="op" :class="{ open, deprecated }" :data-method="method" @keydown="onKey">
    <div class="op-row">
      <button type="button" class="op-toggle" :aria-expanded="open" @click="toggle(op.id)">
        <span class="method-badge">{{ op.method }}</span>
        <span class="op-title">
          <span class="op-path"><span v-for="(p, i) in routeParts" :key="i" :class="{ 'op-path-param': p.param }">{{ p.text }}</span></span>
          <RichText v-if="docs?.summary" class="op-summary" :text="docs.summary" :title="docs.summary" inline :links="false" />
          <span v-else class="op-summary">{{ op.id }}</span>
        </span>
      </button>
      <span v-if="deprecated" class="chip warn" :title="t().deprecatedTitle">{{ t().deprecated }}</span>
      <span v-if="op.security.requestExecution === 'server-only'" class="chip" :title="t().serverOnlyTitle">{{ t().serverOnly }}</span>
      <button
        v-if="summary?.authRequired"
        type="button"
        class="op-lock"
        :class="{ authorized }"
        :title="authorized ? t().lockAuthorizedTitle : t().lockRequiredTitle"
        :aria-label="authorized ? t().authorized : t().lockRequiredLabel"
        @click="store.authOpen = true"
      >
        <Icon :name="authorized ? 'lock' : 'unlock'" :size="16" />
      </button>
      <Icon class="op-chevron chev" :class="{ open }" name="chevron" :size="18" />
    </div>

    <div v-if="open" class="op-body">
      <div class="op-intro">
        <RichText v-if="docs?.description" class="op-description" :text="docs.description" />
        <div class="op-meta">
          <code class="chip mono" :title="t().operationIdTitle">{{ op.id }}</code>
          <span v-if="profileId" class="chip mono" :title="t().profileTitle">{{ profileId }}</span>
          <span v-if="summary?.authRequired" class="chip" :class="authorized ? 'ok' : 'warn'"><Icon :name="authorized ? 'lock' : 'unlock'" :size="12" />{{ authorized ? t().authorizedChip : t().authRequiredChip }}</span>
          <span v-if="summary?.csrfRequired" class="chip warn" :title="t().antiforgeryTitle">{{ t().antiforgery }}</span>
          <span v-if="(summary?.missingCapabilities.length ?? 0) > 0" class="chip bad">{{ t().missingCapabilities(summary!.missingCapabilities.join(", ")) }}</span>
          <button type="button" class="chip chip-button" :title="t().copyLinkTitle" @click="copy(link, t().linkCopied)"><Icon name="link" :size="12" />{{ t().copyLink }}</button>
        </div>
      </div>

      <div class="op-grid">
        <section class="op-col" :aria-label="t().request">
          <div class="col-head">
            <h3>{{ t().request }}</h3>
            <button v-if="someEmpty" type="button" class="btn small ghost" :title="t().fillExamplesTitle" @click="fillExamples"><Icon name="sparkles" :size="13" />{{ t().fillExamples }}</button>
            <button v-if="draft.edited" type="button" class="btn small ghost" :title="t().resetTitle" @click="resetDraft(op)"><Icon name="refresh" :size="13" />{{ t().reset }}</button>
          </div>

          <p v-if="groups.length === 0 && !bodyNode" class="col-note">{{ t().noInputs }}</p>

          <div v-for="g in groups" :key="g.location" class="params">
            <h4 class="sub-head">{{ t().locations[g.location] }}</h4>
            <div class="fields">
              <div v-for="p in g.params" :key="p.name" class="field" :class="{ absent: !p.required && (draft.parameters[p.name] ?? '').length === 0 }">
                <div class="field-label">
                  <label class="field-name" :class="{ deprecated: isDeprecated(p.doc?.text) }" :for="p.repeated ? undefined : 'param-' + domKey + '-' + p.name">{{ p.name }}<span v-if="p.required" class="req" :title="t().required">*</span></label>
                  <span class="field-type">{{ p.node.typeLabel }}{{ p.nullable ? " | null" : "" }}<span v-if="p.repeated" class="chip">{{ t().list }}</span></span>
                </div>
                <div class="field-control">
                  <RichText v-if="p.doc?.summary" class="field-doc" :text="p.doc.summary" inline />
                  <template v-if="p.repeated">
                    <div v-for="(item, i) in listOf(p.name)" :key="i" class="control-row">
                      <ScalarInput class="grow" :node="p.node" :text="item" @update:text="(t) => setListItem(p.name, i, t)" />
                      <button type="button" class="btn small icon ghost" :title="t().removeThisItem" @click="setListItem(p.name, i, undefined)"><Icon name="trash" :size="13" /></button>
                    </div>
                    <div class="row">
                      <button type="button" class="btn small" @click="setListItem(p.name, listOf(p.name).length, '')"><Icon name="plus" :size="13" />{{ t().add }}</button>
                      <span v-if="listOf(p.name).length === 0" class="field-hint">{{ t().sentAs(p.name) }}</span>
                    </div>
                  </template>
                  <template v-else>
                    <div v-if="draft.parameters[p.name] === 'null' && p.nullable" class="input mono faint">null</div>
                    <ScalarInput
                      v-else
                      :id="'param-' + domKey + '-' + p.name"
                      :node="p.node"
                      :text="draft.parameters[p.name] ?? ''"
                      :invalid="paramError(p.name) !== undefined"
                      :optional="!p.required"
                      :placeholder="t().hint(p.node.hint)"
                      @update:text="(t) => setParam(p.name, t)"
                    />
                    <label v-if="p.nullable" class="switch small">
                      <input type="checkbox" :checked="draft.parameters[p.name] === 'null'" @change="setParam(p.name, ($event.target as HTMLInputElement).checked ? 'null' : '')" />{{ t().sendNull }}
                    </label>
                  </template>
                  <span v-if="p.serverDefault !== undefined" class="field-hint">{{ t().serverDefault }} <code>{{ p.serverDefault }}</code></span>
                  <RichText v-if="p.doc?.text" class="field-hint" :text="p.doc.text" inline />
                  <span v-if="paramError(p.name)" class="field-error"><Icon name="alert" :size="13" />{{ paramError(p.name) }}</span>
                </div>
              </div>
            </div>
          </div>

          <div v-if="op.requestBody.kind === 'json' && bodyNode" class="body">
            <div class="sub-head-row">
              <h4 class="sub-head">{{ t().body }}<span v-if="op.requestBody.presence !== 'optional'" class="req" :title="t().required">*</span></h4>
              <code class="body-type" :title="op.requestBody.mediaType">{{ bodyType }}</code>
              <span class="grow" />
              <div class="tabs" role="tablist" :aria-label="t().bodyEditor">
                <button type="button" role="tab" :aria-selected="bodyTab === 'json'" :class="{ on: bodyTab === 'json' }" @click="selectBodyTab('json')">JSON</button>
                <button type="button" role="tab" :aria-selected="bodyTab === 'form'" :class="{ on: bodyTab === 'form' }" @click="selectBodyTab('form')">{{ t().form }}</button>
                <button type="button" role="tab" :aria-selected="bodyTab === 'schema'" :class="{ on: bodyTab === 'schema' }" @click="selectBodyTab('schema')">{{ t().schema }}</button>
              </div>
            </div>
            <RichText v-if="bodyDoc?.summary" class="body-doc" :text="bodyDoc.summary" inline />

            <div v-if="bodyTab === 'schema'" class="type-tree"><TypeTree :node="bodyNode" /></div>
            <template v-else>
              <div v-if="!typedInput && !draft.raw" class="notice warn">
                <Icon name="alert" :size="16" />
                <div class="notice-body"><strong>{{ t().noTypedInput }}</strong><p>{{ t().noTypedInputText }}</p></div>
              </div>
              <div v-if="draft.jsonError" class="notice bad">
                <Icon name="alert" :size="16" />
                <div class="notice-body"><strong>{{ t().jsonDoesNotParse }}</strong><p>{{ draft.jsonError }}</p></div>
              </div>
              <div v-if="bodyTab === 'form'" class="body-form"><ValueEditor :node="bodyNode" :value="draft.body" path="" :errors="bodyErrors" @update="setBody" /></div>
              <template v-else>
                <textarea
                  v-model="draft.bodyText"
                  class="textarea body-text"
                  :class="{ invalid: bodyErrors.size > 0 }"
                  :rows="Math.min(24, Math.max(6, draft.bodyText.split('\n').length + 1))"
                  spellcheck="false"
                  :aria-label="t().requestBodyLabel"
                  @input="edited('/body')"
                />
                <span v-for="[path, message] in bodyErrors" :key="path" class="field-error"><Icon name="alert" :size="13" />{{ path === "" ? "" : path + ": " }}{{ message }}</span>
              </template>
              <div class="body-tools">
                <button v-if="formatted !== undefined" type="button" class="btn small ghost" :title="t().formatTitle" @click="formatBody"><Icon name="braces" :size="13" />{{ t().format }}</button>
                <span class="grow" />
                <label class="switch small" :title="t().sendAsWrittenTitle">
                  <input v-model="draft.raw" type="checkbox" @change="draft.edited = true" />{{ t().sendAsWritten }}
                </label>
              </div>
            </template>
          </div>

          <div class="run">
            <div class="request-line" :class="previewState">
              <span class="method-badge small">{{ op.method }}</span>
              <code v-if="previewState === 'ok'" class="request-line-url">{{ previewed.prepared!.url.href }}</code>
              <span v-else-if="previewState === 'invalid'" class="request-line-url">{{ t().fieldsNeedAttention(errors.length) }}</span>
              <code v-else class="request-line-url">{{ store.apiBase + op.route }}</code>
              <button v-if="previewState === 'ok'" type="button" class="btn small icon ghost" :title="t().copyUrl" @click="copy(previewed.prepared!.url.href, t().urlCopied)"><Icon name="copy" :size="13" /></button>
            </div>
            <details v-if="previewed.prepared" class="fold">
              <summary>{{ t().requestFold }}</summary>
              <table class="kv">
                <tbody>
                  <tr v-for="h in previewHeaders" :key="h.name"><th>{{ h.name }}</th><td :class="{ 't-masked': h.masked }">{{ h.value }}</td></tr>
                  <tr v-for="name in credentialRows" :key="name"><th>{{ name }}</th><td class="t-masked">{{ t().credentialFromAuthorize }}</td></tr>
                  <tr v-if="previewHeaders.length + credentialRows.length === 0"><td class="faint" colspan="2">{{ t().noHeaders }}</td></tr>
                </tbody>
              </table>
              <template v-if="previewed.prepared.bodyText !== undefined">
                <div class="fold-caption">
                  <span>{{ draft.raw ? t().bodyAsWritten : t().bodyFromEncoder }}</span>
                  <button v-if="!store.reveal" type="button" class="btn small ghost" @click="store.reveal = true"><Icon name="eye" :size="13" />{{ t().showValues }}</button>
                </div>
                <CodeBlock v-if="store.reveal" :code="previewed.prepared.bodyText" language="json" :lines="200" revealed wrap />
                <p v-else class="col-note">{{ t().valuesHidden(previewed.prepared.bodyBytes?.byteLength ?? 0) }}</p>
              </template>
            </details>
            <div class="run-buttons">
              <button type="button" class="btn primary large run-button" :disabled="running" :title="t().executeTitle" @click="execute">
                <span v-if="running" class="spinner" /><Icon v-else name="play" :size="14" />{{ t().execute }}
              </button>
              <button v-if="running" type="button" class="btn large" @click="cancel(op)"><Icon name="stop" :size="14" />{{ t().cancel }}</button>
              <span class="run-hint"><span class="kbd">Ctrl</span> + <span class="kbd">Enter</span></span>
            </div>
          </div>
        </section>

        <section class="op-col" :aria-label="t().response">
          <div class="col-head">
            <h3>{{ t().response }}</h3>
            <span v-if="running && run" class="spinner" />
            <button v-if="run && !running" type="button" class="btn small ghost" :title="t().clearTitle" @click="clearRun(op)"><Icon name="x" :size="13" />{{ t().clear }}</button>
          </div>
          <div ref="live">
            <LiveResponse v-if="run" :op="op" :run="run" />
            <div v-else class="col-empty">
              <template v-if="running"><span class="spinner" />{{ t().waiting }}</template>
              <template v-else>{{ t().executeToSee }}</template>
            </div>
          </div>
          <DeclaredResponses :op="op" :run="run" />
        </section>
      </div>
    </div>
  </article>
</template>
