// Explorer state: one store per page. Everything here lives in memory: credentials, drafts and responses are
// gone on reload, and nothing is written to browser storage, the URL (beyond the operation id) or the contract. The display
// preferences (language, theme) are prefs.ts's.
import { computed, markRaw, reactive, type Raw } from "vue";
import { defaultLimits, isFailure, type ContractOperation, type PreparedRequest, type UploadFile } from "@kkdev92/tisilia-runtime";
import { credentialShapes, parseAuthHints, tokenField, type AuthHint, type Credential, type CredentialShape } from "./auth.js";
import { apiBaseOf, buildArgs, execute, executionStatus, loadExplorer, preview, summarize, type BuiltArgs, type Execution, type ExecutionStatus, type ExplorerModel, type OperationSummary } from "./explorer.js";
import { exampleOf, FormSchema, parseFormJson, prettyJson, sanitize, type FormJson } from "./forms.js";
import { t } from "./i18n.js";

export interface Draft {
  formValues: Record<string, string>;
  formUploads: Record<string, { files: readonly File[]; values?: readonly UploadFile[]; loading: boolean; error?: string }>;
  binaryFile: File | undefined;
  binaryBody: Uint8Array | undefined;
  binaryError: string | undefined;
  binaryLoading: boolean;
  /** Editor text per parameter ("" = not sent). */
  parameters: Record<string, string>;
  /** The body as the form holds it; undefined = no body. */
  body: FormJson | undefined;
  /** The body as the JSON editor holds it (when that editor is open). */
  bodyText: string;
  editor: "form" | "json";
  /** Send the edited text as-is instead of through the generated request encoder. */
  raw: boolean;
  /** JSON editor text that does not parse (the form cannot show it). */
  jsonError: string | undefined;
  /** Fields the user has edited, and whether a send was attempted: a missing value is pointed out only after either. */
  touched: Record<string, boolean>;
  attempted: boolean;
  /** Whether anything was changed since the starting values ("Reset" shows then). */
  edited: boolean;
}

export interface Run {
  readonly operationId: string;
  readonly at: Date;
  readonly execution: Execution;
  readonly status: ExecutionStatus;
  /** Public arguments that were sent (typed mode), for the client snippet. */
  readonly args: Record<string, unknown> | undefined;
  /** The credential headers the call went with, values left out: the code shown for the call names them as placeholders. */
  readonly credentials: readonly CredentialShape[];
  /** The response's token field, offered for "Authorize with this token" (the value never shows). */
  readonly token: { readonly field: string; readonly token: string } | undefined;
}

/** What Execute did: sent the call, stopped at inputs that need attention, or did nothing (already running, unknown operation). */
export type SendOutcome = "sent" | "invalid" | "skipped";

const pageUrl = window.location.href;
const routeMeta = document.querySelector<HTMLMetaElement>('meta[name="tisilia-explorer-route"]')?.content;

export const store = reactive({
  loading: true,
  loadError: undefined as string | undefined,
  model: undefined as Raw<ExplorerModel> | undefined,
  schema: undefined as Raw<FormSchema> | undefined,
  summaries: [] as OperationSummary[],
  apiBase: apiBaseOf(pageUrl, routeMeta),
  contractUrl: new URL("./contract", pageUrl).href,
  semanticHashHeader: document.querySelector<HTMLMetaElement>('meta[name="tisilia-explorer-hash-header"]')?.content || "x-tisilia-contract",
  authHints: parseAuthHints(document.querySelector<HTMLMetaElement>('meta[name="tisilia-explorer-auth"]')?.content) as AuthHint[],
  credentials: [] as Credential[],
  reveal: false,
  authOpen: false,
  filter: "",
  /** Expanded operations, collapsed tags and operations being executed (by id). */
  open: {} as Record<string, boolean>,
  closedTags: {} as Record<string, boolean>,
  running: {} as Record<string, boolean>,
  schemasOpen: false,
  /** The operation a deep link (#/op/<id>) or the filter's Enter asked for, until its block has scrolled into view. */
  focusId: undefined as string | undefined,
  toast: undefined as { text: string; detail?: string } | undefined,
});

const drafts = new Map<string, Draft>();
const runs = reactive(new Map<string, Run>());
const controllers = new Map<string, AbortController>();
let toastTimer: ReturnType<typeof setTimeout> | undefined;

export const authorized = computed(() => store.credentials.length > 0);

export async function load(): Promise<void> {
  try {
    const model = await loadExplorer({ baseHref: new URL("./", pageUrl).href });
    store.model = markRaw(model);
    store.schema = markRaw(new FormSchema(model.document));
    store.summaries = summarize(model);
    applyHash();
  } catch (error) {
    store.loadError = error instanceof Error ? error.message : String(error);
  } finally {
    store.loading = false;
  }
}

// ---------------------------------------------------------------- expanding (the operation id is the only thing in the URL)

const hashOf = (id: string): string => "#/op/" + encodeURIComponent(id);

/** Opens or closes an operation; the last one opened is in the URL (#/op/<id>), so it can be bookmarked and shared. */
export function toggle(id: string): void {
  const opening = store.open[id] !== true;
  store.open[id] = opening;
  if (opening && window.location.hash !== hashOf(id)) {
    window.history.replaceState(null, "", hashOf(id));
  } else if (!opening && window.location.hash === hashOf(id)) {
    window.history.replaceState(null, "", window.location.pathname + window.location.search);
  }
}

/** Opens an operation and its tags, puts it in the URL and brings it into view (a deep link, Enter in the filter). */
export function openOperation(id: string): void {
  const summary = store.summaries.find((s) => s.id === id);
  if (summary === undefined) {
    return;
  }
  store.open[id] = true;
  for (const tag of summary.tags.length > 0 ? summary.tags : ["default"]) {
    store.closedTags[tag] = false;
  }
  store.focusId = id;
  if (window.location.hash !== hashOf(id)) {
    window.history.replaceState(null, "", hashOf(id));
  }
}

/** A deep link shows its operation even when the filter would hide it. */
export function applyHash(): void {
  const hash = window.location.hash;
  if (!hash.startsWith("#/op/")) {
    return;
  }
  let id: string;
  try {
    id = decodeURIComponent(hash.slice(5));
  } catch {
    return; // a malformed escape in a hand-edited URL
  }
  if (store.summaries.some((s) => s.id === id)) {
    store.filter = "";
    openOperation(id);
  }
}

window.addEventListener("hashchange", applyHash);

// ---------------------------------------------------------------- drafts (inputs are live from the start: no "Try it out" step)

function freshDraft(op: ContractOperation): Draft {
  const body = op.requestBody.kind === "json" && store.schema !== undefined ? exampleOf(store.schema.body(op.requestBody.use)) : undefined;
  return { formValues: Object.create(null) as Record<string, string>, formUploads: Object.create(null) as Draft["formUploads"], parameters: {}, body, bodyText: body === undefined ? "" : prettyJson(sanitize(body)), editor: "json", raw: false, jsonError: undefined, touched: {}, attempted: false, edited: false, binaryFile: undefined, binaryBody: undefined, binaryError: undefined, binaryLoading: false };
}

export async function selectFormFiles(draft: Draft, name: string, files: readonly File[]): Promise<void> {
  draft.formUploads[name] = { files, loading: files.length > 0 };
  const selection = draft.formUploads[name]!;
  draft.edited = true;
  draft.touched["/body"] = true;
  try {
    const total = Object.values(draft.formUploads).reduce((n, s) => n + s.files.reduce((m, f) => m + f.size, 0), 0);
    if (total > defaultLimits.maxBodyBytes) { selection.error = t().uploadTooLarge; return; }
    const values: UploadFile[] = [];
    for (const file of files) { values.push({ fileName: file.name, bytes: new Uint8Array(await file.arrayBuffer()) }); }
    if (draft.formUploads[name] === selection) { selection.values = values; }
  } catch { if (draft.formUploads[name] === selection) { selection.error = t().uploadReadFailed; } }
  finally { selection.loading = false; }
}

/** Read a selected finite file in memory, rejecting oversized inputs before allocating their bytes. */
export async function selectBinaryFile(draft: Draft, file: File | undefined): Promise<void> {
  draft.binaryFile = file;
  draft.binaryBody = undefined;
  draft.binaryError = undefined;
  draft.binaryLoading = file !== undefined;
  draft.edited = true;
  draft.touched["/body"] = true;
  if (file === undefined) { return; }
  try {
    if (file.size > defaultLimits.maxBodyBytes) { draft.binaryError = t().uploadTooLarge; return; }
    const bytes = new Uint8Array(await file.arrayBuffer());
    if (draft.binaryFile === file) { draft.binaryBody = bytes; }
  } catch {
    if (draft.binaryFile === file) { draft.binaryError = t().uploadReadFailed; }
  } finally {
    if (draft.binaryFile === file) { draft.binaryLoading = false; }
  }
}

export function draftOf(op: ContractOperation): Draft {
  let draft = drafts.get(op.id);
  if (draft === undefined) {
    draft = reactive<Draft>(freshDraft(op)) as Draft;
    drafts.set(op.id, draft);
  }
  return draft;
}

/** "Reset": every input of the operation goes back to its starting value (the body to an example). */
export function resetDraft(op: ContractOperation): void {
  Object.assign(draftOf(op), freshDraft(op));
}

/** Switches the body editor; JSON text that does not parse keeps the JSON editor open. */
export function switchEditor(draft: Draft, editor: "form" | "json"): void {
  if (editor === draft.editor) {
    return;
  }
  if (editor === "json") {
    draft.bodyText = draft.body === undefined ? "" : prettyJson(sanitize(draft.body));
    draft.editor = "json";
    return;
  }
  const parsed = parseFormJson(draft.bodyText);
  if ("error" in parsed) {
    draft.jsonError = parsed.error;
    return;
  }
  draft.body = parsed.value;
  draft.jsonError = undefined;
  draft.editor = "form";
}

/** The arguments the draft stands for, with every input error (built by the codecs, never by the page). */
export function argsOf(op: ContractOperation, draft: Draft): BuiltArgs {
  const model = store.model!;
  if (op.requestBody.kind === "form") {
    const built = buildArgs(model, op, { parameters: draft.parameters, body: "", formValues: draft.formValues, formFiles: Object.fromEntries(Object.entries(draft.formUploads).filter(([, s]) => s.values !== undefined).map(([name, s]) => [name, s.values!])) });
    const errors = Object.entries(draft.formUploads).flatMap(([name, s]) => s.loading || s.error !== undefined ? [{ path: "/body/" + name, message: s.loading ? t().uploadReading : s.error! }] : []);
    return { args: built.args, errors: [...errors, ...built.errors] };
  }
  if (op.requestBody.kind === "binary") {
    const built = buildArgs(model, op, { parameters: draft.parameters, body: "", ...(draft.binaryBody === undefined ? {} : { bodyBytes: draft.binaryBody }) });
    const issue = draft.binaryLoading ? t().uploadReading : draft.binaryError;
    return issue === undefined ? built : { args: built.args, errors: [{ path: "/body", message: issue }, ...built.errors] };
  }
  if (draft.editor === "json") {
    const parsed = parseFormJson(draft.bodyText);
    if ("error" in parsed && draft.bodyText.trim().length > 0) {
      const built = buildArgs(model, op, { parameters: draft.parameters, body: "" });
      return { args: built.args, errors: [...built.errors.filter((e) => !e.path.startsWith("/body")), { path: "/body", message: "invalid JSON: " + parsed.error, code: "invalid-json" }] };
    }
    return buildArgs(model, op, { parameters: draft.parameters, body: draft.bodyText });
  }
  return buildArgs(model, op, { parameters: draft.parameters, body: "", ...(draft.body !== undefined ? { bodyJson: sanitize(draft.body) } : {}) });
}

export function rawBodyOf(draft: Draft): string {
  return draft.editor === "json" ? draft.bodyText : draft.body === undefined ? "" : prettyJson(sanitize(draft.body));
}

/** The request Execute would send, written by the same request encoder — credentials are added only when sending. */
export function previewOf(op: ContractOperation, draft: Draft, built: BuiltArgs): { prepared?: PreparedRequest; error?: string } {
  const operation = store.model?.registry.operations.get(op.id);
  if (operation === undefined) {
    return { error: "operation not in the registry" };
  }
  if (!draft.raw && built.errors.length > 0) {
    return { error: built.errors[0]!.path + ": " + built.errors[0]!.message };
  }
  try {
    return { prepared: preview(operation, built.args, { baseUrl: store.apiBase, ...(draft.raw ? { rawBody: rawBodyOf(draft) } : {}) }) };
  } catch (error) {
    return { error: error instanceof Error ? error.message : String(error) };
  }
}

// ---------------------------------------------------------------- executing

export async function send(op: ContractOperation): Promise<SendOutcome> {
  const operation = store.model?.registry.operations.get(op.id);
  if (operation === undefined || store.running[op.id] === true) {
    return "skipped";
  }
  const draft = draftOf(op);
  const built = argsOf(op, draft);
  if (!draft.raw && built.errors.length > 0) {
    // the fields say what is wrong, the first one gets the focus, and the request line counts them: no toast on top
    draft.attempted = true;
    return "invalid";
  }
  const credentials = store.credentials;
  const controller = new AbortController();
  controllers.set(op.id, controller);
  store.running[op.id] = true;
  try {
    const execution = await execute(operation, built.args, {
      baseUrl: store.apiBase,
      credentials,
      expectedSemanticHash: store.model!.document.semanticHash,
      semanticHashHeader: store.semanticHashHeader,
      signal: controller.signal,
      ...(draft.raw ? { rawBody: rawBodyOf(draft) } : {}),
    });
    const result = execution.result;
    const data = result !== undefined && !isFailure(result) && "data" in result ? (result as { data?: unknown }).data : undefined;
    runs.set(
      op.id,
      markRaw({ operationId: op.id, at: new Date(), execution, status: executionStatus(execution), args: draft.raw ? undefined : built.args, credentials: credentialShapes(credentials), token: tokenField(data) }),
    );
  } finally {
    store.running[op.id] = false;
    controllers.delete(op.id);
  }
  return "sent";
}

export function cancel(op: ContractOperation): void {
  controllers.get(op.id)?.abort();
}

/** "Clear": forgets the operation's last response. */
export function clearRun(op: ContractOperation): void {
  runs.delete(op.id);
}

// ---------------------------------------------------------------- credentials (memory only)

export function authorize(credentials: Credential[]): void {
  store.credentials = credentials;
}

/** Sign out: credentials and every response are forgotten together. */
export function signOut(): void {
  store.credentials = [];
  runs.clear();
  notify(t().signedOut);
}

export function useToken(run: Run): void {
  if (run.token === undefined) {
    return;
  }
  store.credentials = [...store.credentials.filter((c) => c.kind !== "bearer" && c.kind !== "basic"), { kind: "bearer", token: run.token.token }];
  notify(t().authorizedWithToken(run.token.field));
}

// ---------------------------------------------------------------- page

export function notify(text: string, detail?: string): void {
  store.toast = detail === undefined ? { text } : { text, detail };
  if (toastTimer !== undefined) {
    clearTimeout(toastTimer);
  }
  toastTimer = setTimeout(() => (store.toast = undefined), detail === undefined ? 2600 : 9000);
}

export { runs };
