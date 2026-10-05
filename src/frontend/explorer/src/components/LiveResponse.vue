<script setup lang="ts">
// The last call of an operation, answer first: the status (coloured by class), the case it decoded as, timing and size, what to do
// about a failure, and the body; folded below, the response headers and the request as it was sent (URL, and the call as code:
// curl, fetch, the generated client). Values stay masked unless shown for the session, long bodies show their first
// lines, and the decoded view shows what the generated client hands to code, kinds included.
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { isFailure, parseJson, type BufferedFile, type ContractOperation, type JsonValue } from "@kkdev92/tisilia-runtime";
import { DownloadLease, downloadDiagnosticHeaders, downloadPolicy } from "../download.js";
import { describeValue, prettyWire, redactHeaders, redactTree, responseOf, type DisplayNode } from "../explorer.js";
import { formatNumber, t } from "../i18n.js";
import { snippet, type SnippetKind } from "../snippets.js";
import { store, useToken, type Run } from "../state.js";
import CodeBlock from "./CodeBlock.vue";
import DisplayTree from "./DisplayTree.vue";
import Icon from "./Icon.vue";

const props = defineProps<{ op: ContractOperation; run: Run }>();
const code = ref<SnippetKind>("curl");
const bodyView = ref<"json" | "decoded">("json");

const reasons: Record<number, string> = { 200: "OK", 201: "Created", 202: "Accepted", 204: "No Content", 301: "Moved Permanently", 302: "Found", 304: "Not Modified", 400: "Bad Request", 401: "Unauthorized", 403: "Forbidden", 404: "Not Found", 405: "Method Not Allowed", 406: "Not Acceptable", 409: "Conflict", 410: "Gone", 412: "Precondition Failed", 413: "Content Too Large", 415: "Unsupported Media Type", 422: "Unprocessable Content", 429: "Too Many Requests", 500: "Internal Server Error", 502: "Bad Gateway", 503: "Service Unavailable", 504: "Gateway Timeout" };

const result = computed(() => props.run.execution.result);
const raw = computed(() => props.run.execution.raw);
const file = computed<BufferedFile | undefined>(() => {
  const r = result.value;
  return raw.value?.kind === "raw" && raw.value.bodyKind === "binary" && r?.kind === "response" && "data" in r ? r.data as BufferedFile : undefined;
});
const filePolicy = computed(() => downloadPolicy(props.op, store.reveal));
const downloads = new DownloadLease();
watch(() => props.run, () => downloads.dispose());
watch(() => store.reveal, () => { if (!store.reveal) { downloads.dispose(); } });
onBeforeUnmount(() => downloads.dispose());
function saveFile(): void {
  if (file.value === undefined || !filePolicy.value.allowed || result.value?.kind !== "response" || result.value.status === 206) { return; }
  if (window.confirm(t().confirmDownload)) { downloads.save(file.value, filePolicy.value.filenameAllowed); }
}
const prepared = computed(() => props.run.execution.prepared);
const metadata = computed(() => {
  const r = raw.value;
  return r === undefined ? undefined : r.kind === "raw" ? r.metadata : "metadata" in r ? r.metadata : undefined;
});
const received = computed(() => responseOf(props.run.execution));
const status = computed(() => received.value.status);
const statusClass = computed(() => (status.value === undefined ? "s-none" : "s" + String(status.value)[0]));
const statusText = computed(() => (status.value === undefined ? t().noResponse : [String(status.value), reasons[status.value]].filter((t) => t !== undefined).join(" ")));
const undocumented = computed(() => result.value !== undefined && result.value.kind === "unexpected-response");
const bytes = computed<Uint8Array | undefined>(() => {
  const r = raw.value;
  return r === undefined ? undefined : r.kind === "raw" ? r.body : "rawBody" in r ? (r as { rawBody?: Uint8Array }).rawBody : undefined;
});
const size = computed(() => {
  const n = metadata.value?.bodyBytes ?? bytes.value?.byteLength;
  return n === undefined ? "" : n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`;
});

const decoded = computed<DisplayNode | undefined>(() => {
  if (file.value !== undefined) { return undefined; }
  const r = result.value;
  if (r === undefined || isFailure(r) || !("data" in r)) {
    return undefined;
  }
  return redactTree(describeValue((r as { data: unknown }).data, ""), store.reveal ? "reveal" : "mask");
});

const bodyText = computed<{ text: string; json: boolean } | undefined>(() => {
  if (file.value !== undefined) { return undefined; }
  const b = bytes.value;
  if (b === undefined || b.byteLength === 0) {
    return undefined;
  }
  let text: string;
  try {
    text = new TextDecoder("utf-8", { fatal: true }).decode(b);
  } catch {
    return { text: t().bytesNotUtf8(b.byteLength), json: false };
  }
  let json: JsonValue;
  try {
    json = parseJson(text);
  } catch {
    // text that is not JSON cannot be masked field by field: it is hidden whole unless shown
    return { text: store.reveal ? text : t().bytesNotJson(b.byteLength), json: false };
  }
  return { text: prettyWire(json, store.reveal ? "reveal" : "mask"), json: true };
});

const headers = computed(() => redactHeaders(file.value !== undefined && raw.value?.kind === "raw" ? raw.value.headers : downloadDiagnosticHeaders(props.op, metadata.value?.headers ?? []), store.reveal ? "reveal" : "mask").map(h =>
  file.value !== undefined && h.name === "content-disposition" && !filePolicy.value.filenameAllowed ? { ...h, value: "•••", masked: true } : h));

const snippetText = computed(() =>
  prepared.value === undefined
    ? ""
    : snippet(code.value, prepared.value, {
        apiId: store.model!.document.apiId,
        operationId: props.op.id,
        baseUrl: store.apiBase,
        ...(props.run.args !== undefined ? { args: props.run.args } : {}),
        // the credentials this call went with — not the ones set since
        credentials: props.run.credentials,
        reveal: store.reveal,
      }),
);

const failure = computed<{ title: string; detail: string; hint: string; authorize?: boolean } | undefined>(() => {
  const r = result.value;
  const e = props.run.execution;
  if (e.error !== undefined) {
    return { title: t().failNotSent, detail: e.error, hint: t().failNotSentHint };
  }
  if (r === undefined || !isFailure(r)) {
    return undefined;
  }
  switch (r.kind) {
    case "unexpected-response": {
      const credentials = r.metadata?.status === 401 || r.metadata?.status === 403;
      return {
        title: t().failUndocumented(r.reason === "undeclared-media"),
        detail: `HTTP ${r.metadata?.status ?? "?"}${r.metadata?.mediaType ? " · " + r.metadata.mediaType : ""}`,
        hint: credentials ? t().failCredentialsHint : r.metadata?.status === 404 ? t().failNotFoundHint : t().failUndeclaredHint,
        authorize: credentials,
      };
    }
    case "codec-failure":
      return { title: t().failDecode, detail: t().failDecodeDetail(r.code, r.path, r.caseId), hint: t().failDecodeHint };
    case "transport-failure":
      return {
        title: r.reason === "request-encoding" ? t().failEncode : t().failFetch,
        detail: r.message,
        hint: r.reason === "network" ? t().failNetworkHint : r.reason === "redirect" ? t().failRedirectHint : "",
      };
    case "timeout":
      return { title: t().failTimeout, detail: t().failTimeoutDetail(r.timeoutMs), hint: "" };
    case "cancelled":
      return { title: t().failCancelled, detail: "", hint: "" };
    case "limit-failure":
      return { title: t().failLimit, detail: r.limit, hint: t().failLimitHint };
    case "contract-mismatch":
      return { title: t().failMismatch, detail: t().failMismatchDetail, hint: t().failMismatchHint };
    default:
      return { title: (r as { kind: string }).kind, detail: "", hint: "" };
  }
});
// a declared 401/403 case decodes fine, but the next step is the same: Authorize
const declaredDenied = computed(() => failure.value === undefined && (status.value === 401 || status.value === 403));
</script>

<template>
  <div class="result">
    <div class="result-status">
      <span class="status" :class="statusClass">{{ statusText }}</span>
      <code v-if="received.caseId" class="chip mono accent" :title="t().caseTitle">{{ received.caseId }}</code>
      <span v-if="undocumented" class="chip warn" :title="t().undocumentedTitle">{{ t().undocumented }}</span>
      <span v-if="run.execution.mode === 'raw'" class="chip info" :title="t().rawTitle">{{ t().raw }}</span>
      <span class="grow" />
      <span class="result-meta"><Icon name="clock" :size="13" />{{ formatNumber(Math.round(run.execution.elapsedMs)) }} ms<template v-if="size"> · {{ size }}</template></span>
    </div>

    <div v-if="failure" class="notice" :class="result?.kind === 'cancelled' ? '' : 'bad'">
      <Icon name="alert" :size="16" />
      <div class="notice-body">
        <strong>{{ failure.title }}</strong>
        <p v-if="failure.detail" class="mono">{{ failure.detail }}</p>
        <p v-if="failure.hint">{{ failure.hint }}</p>
      </div>
      <button v-if="failure.authorize" type="button" class="btn small" @click="store.authOpen = true"><Icon name="lock" :size="13" />{{ t().authorize }}</button>
    </div>
    <div v-else-if="declaredDenied" class="notice warn">
      <Icon name="lock" :size="16" />
      <div class="notice-body"><strong>{{ t().notAuthorized }}</strong><p>{{ t().notAuthorizedText }}</p></div>
      <button type="button" class="btn small" @click="store.authOpen = true"><Icon name="lock" :size="13" />{{ t().authorize }}</button>
    </div>

    <div v-if="run.token" class="notice ok">
      <Icon name="key" :size="16" />
      <div class="notice-body">
        <strong>{{ t().tokenFound(run.token.field) }}</strong>
        <p>{{ t().tokenFoundText }}</p>
      </div>
      <button type="button" class="btn small ok" @click="useToken(run)"><Icon name="lock" :size="13" />{{ t().authorizeWithIt }}</button>
    </div>

    <div v-if="file" class="result-body">
      <p>{{ file.contentType }} · {{ size }}</p>
      <p>{{ status === 206 ? t().partialFile : t().bufferedFile }}</p>
      <p v-if="filePolicy.filenameAllowed && file.suggestedFileName">{{ file.suggestedFileName }}</p>
      <button type="button" class="btn small ghost" @click="store.reveal = !store.reveal">{{ store.reveal ? t().hideValues : t().showValues }}</button>
      <button type="button" class="btn small" :disabled="!filePolicy.allowed || status === 206" @click="saveFile">{{ t().saveFile }}</button>
    </div>
    <div v-else-if="bodyText || decoded" class="result-body">
      <div class="result-body-head">
        <div v-if="decoded" class="tabs" role="tablist" :aria-label="t().bodyView">
          <button type="button" role="tab" :aria-selected="bodyView === 'json'" :class="{ on: bodyView === 'json' }" @click="bodyView = 'json'">JSON</button>
          <button type="button" role="tab" :aria-selected="bodyView === 'decoded'" :class="{ on: bodyView === 'decoded' }" @click="bodyView = 'decoded'">{{ t().decoded }}</button>
        </div>
        <span v-else class="sub-head">{{ t().body }}</span>
        <span class="grow" />
        <button type="button" class="btn small ghost values-toggle" :title="store.reveal ? t().hideValuesTitle : t().showValuesTitle" @click="store.reveal = !store.reveal">
          <Icon :name="store.reveal ? 'eye-off' : 'eye'" :size="13" />{{ store.reveal ? t().hideValues : t().showValues }}
        </button>
      </div>
      <div v-if="bodyView === 'decoded' && decoded" class="tree"><DisplayTree :node="decoded" /></div>
      <CodeBlock v-else-if="bodyText" :code="bodyText.text" :language="bodyText.json ? 'json' : 'text'" :revealed="store.reveal" :lines="300" wrap />
    </div>
    <p v-else-if="!failure" class="col-note">{{ t().noBody }}</p>

    <details v-if="headers.length > 0" class="fold">
      <summary>{{ t().responseHeaders }} <span class="count">{{ headers.length }}</span></summary>
      <table class="kv">
        <tbody>
          <tr v-for="h in headers" :key="h.name"><th>{{ h.name }}</th><td :class="{ 't-masked': h.masked }">{{ h.value }}</td></tr>
        </tbody>
      </table>
    </details>

    <details class="fold">
      <summary>{{ t().requestAndCode }} <span class="faint">curl · fetch · {{ t().tisiliaClient }}</span></summary>
      <template v-if="prepared">
        <p class="request-sent"><span class="method-badge small">{{ prepared.method }}</span><code>{{ prepared.url.href }}</code></p>
        <div class="tabs" role="tablist" :aria-label="t().code">
          <button type="button" role="tab" :aria-selected="code === 'curl'" :class="{ on: code === 'curl' }" @click="code = 'curl'">curl</button>
          <button type="button" role="tab" :aria-selected="code === 'fetch'" :class="{ on: code === 'fetch' }" @click="code = 'fetch'">fetch</button>
          <button type="button" role="tab" :aria-selected="code === 'client'" :class="{ on: code === 'client' }" @click="code = 'client'">{{ t().tisiliaClient }}</button>
        </div>
        <CodeBlock :code="snippetText" :language="code === 'curl' ? 'shell' : 'ts'" :revealed="store.reveal" wrap />
      </template>
      <p v-else class="col-note">{{ t().requestNotSent }}</p>
    </details>
  </div>
</template>
