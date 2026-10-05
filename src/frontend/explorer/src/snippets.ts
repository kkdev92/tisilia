// Code for the request on screen: curl, fetch, and the call through the generated client. Credentials appear only as
// placeholders ($TOKEN …) — the values are never written into text that leaves the page — and request values follow the screen's
// redaction mode: masked unless revealed for this session.
import {
  encodeBase64,
  formatDateOnly,
  formatDateTimeLocalWire,
  formatDateTimeOffset,
  formatDateTimeUnspecified,
  formatDateTimeUtc,
  formatDecimal,
  formatDuration,
  formatTimeOnly,
  isDecimal,
  TisiliaMap,
  writeJson,
  type JsonValue,
  type PreparedRequest,
} from "@kkdev92/tisilia-runtime";
import type { CredentialShape } from "./auth.js";

// ---------------------------------------------------------------- names (Tisilia.Generator TsNames)

const reserved = new Set([
  "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with", "as", "implements", "interface", "let", "package", "private", "protected", "public", "static", "yield", "any", "boolean", "constructor", "declare", "get", "module", "require", "number", "set", "string", "symbol", "type", "from", "of", "async", "await", "namespace", "readonly", "unknown", "never", "object", "bigint", "undefined", "NaN", "Infinity", "globalThis", "eval", "arguments",
]);
const identifierStart = /^[\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Nl}_$]$/u;
const identifierPart = /^[\p{Lu}\p{Ll}\p{Lt}\p{Lm}\p{Lo}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}_$]$/u;

// per UTF-16 code unit, as the generator's char-based check: a surrogate is never part of a name
function isIdentifier(name: string): boolean {
  const units = name.split("");
  return units.length > 0 && identifierStart.test(units[0]!) && units.every((u) => identifierPart.test(u));
}

const firstCase = (s: string, upper: boolean): string => {
  if (s.length === 0) {
    return s;
  }
  const c = upper ? s[0]!.toUpperCase() : s[0]!.toLowerCase();
  return (c.length === 1 ? c : s[0]!) + s.slice(1);
};

/** `users.get` → `usersGet`, as the generated client names its methods. */
export function operationMethodName(operationId: string): string {
  const parts = operationId.split(/[.\-_:/@]/).filter((p) => p.length > 0);
  let name = parts.map((p, i) => firstCase(p, i > 0)).join("");
  if (!isIdentifier(name) || reserved.has(name)) {
    name = "op" + firstCase(name, true);
  }
  return name;
}

/** `sample-api` → `createSampleApiClient`. */
export function clientFactoryName(apiId: string): string {
  return "create" + firstCase(operationMethodName(apiId), true) + "Client";
}

export function propertyKey(name: string): string {
  return isIdentifier(name) && !reserved.has(name) ? name : JSON.stringify(name);
}

// ---------------------------------------------------------------- TypeScript literals of domain values

const dateHelpers: Readonly<Record<string, readonly [string, (v: never) => string]>> = {
  "date-only": ["parseDateOnly", formatDateOnly as (v: never) => string],
  "time-only": ["parseTimeOnly", formatTimeOnly as (v: never) => string],
  "datetime-utc": ["parseDateTimeUtc", formatDateTimeUtc as (v: never) => string],
  "datetime-unspecified": ["parseDateTimeUnspecified", formatDateTimeUnspecified as (v: never) => string],
  "datetime-local-wire": ["parseDateTimeLocalWire", formatDateTimeLocalWire as (v: never) => string],
  "datetime-offset": ["parseDateTimeOffset", formatDateTimeOffset as (v: never) => string],
  duration: ["parseDuration", formatDuration as (v: never) => string],
};

const jsonKinds = new Set(["null", "boolean", "string", "number", "array", "object"]);

/** A TypeScript expression that builds a public domain value, with the runtime helpers it needs. */
export function tsLiteral(value: unknown, helpers: Set<string>, indent = ""): string {
  const inner = indent + "  ";
  if (value === null) {
    return "null";
  }
  switch (typeof value) {
    case "undefined":
      return "undefined";
    case "boolean":
      return value ? "true" : "false";
    case "bigint":
      return value.toString() + "n";
    case "number":
      return Number.isNaN(value) ? "Number.NaN" : value === Infinity ? "Number.POSITIVE_INFINITY" : value === -Infinity ? "Number.NEGATIVE_INFINITY" : Object.is(value, -0) ? "-0" : String(value);
    case "string":
      return JSON.stringify(value);
    case "object":
      break;
    default:
      return String(value);
  }
  if (value instanceof Uint8Array) {
    helpers.add("decodeBase64");
    return `decodeBase64(${JSON.stringify(encodeBase64(value))})`;
  }
  if (isDecimal(value)) {
    helpers.add("decimalFromString");
    return `decimalFromString(${JSON.stringify(formatDecimal(value))})`;
  }
  if (value instanceof TisiliaMap || value instanceof Map) {
    const entries = [...(value as Map<unknown, unknown>).entries()];
    return entries.length === 0 ? "new Map()" : "new Map([\n" + entries.map(([k, v]) => `${inner}[${tsLiteral(k, helpers, inner)}, ${tsLiteral(v, helpers, inner)}]`).join(",\n") + `,\n${indent}])`;
  }
  if (Array.isArray(value)) {
    return value.length === 0 ? "[]" : "[\n" + value.map((v) => inner + tsLiteral(v, helpers, inner)).join(",\n") + `,\n${indent}]`;
  }
  const record = value as Record<string, unknown>;
  const kind = record["kind"];
  if (typeof kind === "string" && kind in dateHelpers && Object.keys(record).length > 1) {
    const [name, format] = dateHelpers[kind]!;
    helpers.add(name);
    return `${name}(${JSON.stringify(format(value as never))})`;
  }
  if (typeof kind === "string" && jsonKinds.has(kind) && ("value" in record || "text" in record || "items" in record || "entries" in record || kind === "null")) {
    // a lossless JSON value (JsonElement, JsonNode): built from its text
    helpers.add("parseJson");
    return `parseJson(${JSON.stringify(writeJson(value as JsonValue))})`;
  }
  const keys = Object.keys(record).filter((k) => record[k] !== undefined);
  return keys.length === 0 ? "{}" : "{\n" + keys.map((k) => `${inner}${propertyKey(k)}: ${tsLiteral(record[k], helpers, inner)}`).join(",\n") + `,\n${indent}}`;
}

// ---------------------------------------------------------------- snippets

export type SnippetKind = "client" | "fetch" | "curl";

export interface SnippetOptions {
  readonly apiId: string;
  readonly operationId: string;
  /** Where the API is (the client's baseUrl): the origin plus any PathBase. */
  readonly baseUrl: string;
  /** Public domain arguments of the call (the typed request); absent in raw mode. */
  readonly args?: Record<string, unknown>;
  /** The credential headers the call goes with (named as placeholders, never with their values). */
  readonly credentials: readonly CredentialShape[];
  readonly reveal: boolean;
}

const knownSafeHeaders = new Set(["content-type", "accept", "accept-language", "content-length"]);
const hidden = "•••";

/** Placeholder headers for the credentials in use: the code names them, the values stay on this page. */
function credentialPlaceholders(credentials: readonly CredentialShape[]): { readonly name: string; readonly value: string; readonly variable: string }[] {
  const out = new Map<string, { name: string; value: string; variable: string }>();
  for (const c of credentials) {
    switch (c.kind) {
      case "bearer":
        out.set("authorization", { name: "authorization", value: "Bearer $TOKEN", variable: "TOKEN" });
        break;
      case "basic":
        out.set("authorization", { name: "authorization", value: "Basic $BASIC_CREDENTIALS", variable: "BASIC_CREDENTIALS" });
        break;
      case "api-key":
        out.set(c.header.toLowerCase(), { name: c.header.toLowerCase(), value: "$API_KEY", variable: "API_KEY" });
        break;
      case "header": {
        const variable = c.name.toUpperCase().replace(/[^A-Z0-9]+/g, "_").replace(/^_+|_+$/g, "") || "HEADER";
        out.set(c.name.toLowerCase(), { name: c.name.toLowerCase(), value: "$" + variable, variable });
        break;
      }
    }
  }
  return [...out.values()];
}

const quoteShell = (s: string): string => "'" + s.replace(/'/g, "'\\''") + "'";

export function curlSnippet(prepared: PreparedRequest, options: SnippetOptions): string {
  const lines = [`curl -X ${prepared.method} ${quoteShell(prepared.url.href)}`];
  for (const [name, value] of prepared.headers) {
    lines.push(`  -H ${quoteShell(`${name}: ${options.reveal || knownSafeHeaders.has(name) ? value : hidden}`)}`);
  }
  for (const p of credentialPlaceholders(options.credentials)) {
    lines.push(`  -H "${p.name}: ${p.value}"`);
  }
  if (prepared.bodyText !== undefined) {
    lines.push(`  --data-raw ${quoteShell(options.reveal ? prepared.bodyText : hidden)}`);
  }
  return lines.join(" \\\n");
}

export function fetchSnippet(prepared: PreparedRequest, options: SnippetOptions): string {
  const headers = prepared.headers.map(([n, v]) => `    ${JSON.stringify(n)}: ${JSON.stringify(options.reveal || knownSafeHeaders.has(n) ? v : hidden)},`);
  for (const p of credentialPlaceholders(options.credentials)) {
    headers.push(`    ${JSON.stringify(p.name)}: \`${p.value.replace("$" + p.variable, "${" + p.variable + "}")}\`,`);
  }
  const lines = [`const response = await fetch(${JSON.stringify(prepared.url.href)}, {`, `  method: ${JSON.stringify(prepared.method)},`];
  if (headers.length > 0) {
    lines.push("  headers: {", ...headers, "  },");
  }
  if (prepared.bodyText !== undefined) {
    // the exact text the generated encoder wrote: JSON.stringify would round int64 and decimal values
    lines.push(`  body: ${JSON.stringify(options.reveal ? prepared.bodyText : hidden)},`);
  }
  lines.push("});");
  return lines.join("\n");
}

/** The call through the generated client (typed mode): the same arguments the Explorer built, as TypeScript. */
export function clientSnippet(options: SnippetOptions): string {
  const helpers = new Set<string>();
  const factory = clientFactoryName(options.apiId);
  const method = operationMethodName(options.operationId);
  const credentials = credentialPlaceholders(options.credentials);
  let args: string;
  if (options.args === undefined) {
    args = "{ /* raw mode: the generated client sends what its encoder writes, not edited text */ }";
  } else if (options.reveal) {
    args = tsLiteral(options.args, helpers);
  } else {
    const keys = Object.keys(options.args).filter((k) => options.args![k] !== undefined);
    args = keys.length === 0 ? "{}" : "{ " + keys.map((k) => `${propertyKey(k)}: …`).join(", ") + " }";
  }
  const lines: string[] = [`import { ${factory} } from "./api/index.js";`];
  if (helpers.size > 0) {
    lines.push(`import { ${[...helpers].sort().join(", ")} } from "@kkdev92/tisilia-runtime";`);
  }
  lines.push("", `const client = ${factory}({`, `  baseUrl: ${JSON.stringify(options.baseUrl)},`);
  if (credentials.length > 0) {
    lines.push(`  credentialProvider: () => [${credentials.map((p) => `[${JSON.stringify(p.name)}, \`${p.value.replace("$" + p.variable, "${" + p.variable + "}")}\`]`).join(", ")}],`);
  }
  lines.push("});", "");
  if (options.args !== undefined && !options.reveal && Object.keys(options.args).length > 0) {
    lines.push("// values hidden: show values to include them");
  }
  lines.push(`const result = await client.${method}(${args});`);
  lines.push(`if (result.kind === "response") {`, `  console.log(result.status, result.caseId, "data" in result ? result.data : undefined);`, `} else {`, `  console.error(result.kind);`, `}`);
  return lines.join("\n");
}

export function snippet(kind: SnippetKind, prepared: PreparedRequest, options: SnippetOptions): string {
  switch (kind) {
    case "curl":
      return curlSnippet(prepared, options);
    case "fetch":
      return fetchSnippet(prepared, options);
    case "client":
      return clientSnippet(options);
  }
}
