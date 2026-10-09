import { CodecError } from "../codec/errors.js";
import { encodePathSegment, encodeQueryComponent, validateBaseUrl, type BuiltUrl, type QueryEntry } from "./url.js";

export interface RouteParameter {
  readonly kind: "parameter";
  readonly parameterId: string;
  readonly name: string;
  readonly optional: boolean;
  readonly catchAll: "none" | "encode-slashes" | "preserve-slashes";
  readonly hasDefault: boolean;
  readonly defaultValue?: string;
  readonly policies: readonly string[];
}
export type ResolvedRoutePart = { readonly kind: "literal" | "separator"; readonly value: string } | RouteParameter;
export interface RoutePlan { readonly segments: readonly { readonly parts: readonly ResolvedRoutePart[] }[] }

function error(message: string, id = ""): never { throw new CodecError("grammar", id, message); }

export function validateRoutePlan(plan: RoutePlan, route: string, parameters: readonly { readonly id?: string; readonly name: string; readonly location: string; readonly presence: string }[]): void {
  if (!plan || !Array.isArray(plan.segments)) { error("contract 0.1 requires a resolved routePlan; re-export with matching Tisilia tooling"); }
  const used = new Set<string>();
  for (const [i, segment] of plan.segments.entries()) {
    if (!Array.isArray(segment.parts) || segment.parts.length === 0) { error("empty or invalid route segment"); }
    for (const [j, part] of segment.parts.entries()) {
      if (part.kind === "parameter") {
        const matches = parameters.filter(p => p.location === "path" && p.id === part.parameterId && p.name.toUpperCase() === part.name.toUpperCase());
        if (matches.length !== 1 || used.has(part.parameterId)) { error("missing or duplicate resolved path parameter", part.parameterId); }
        used.add(part.parameterId);
        if (typeof part.optional !== "boolean" || typeof part.hasDefault !== "boolean" || !Array.isArray(part.policies) || part.policies.some((p: unknown) => typeof p !== "string")
            || !["none", "encode-slashes", "preserve-slashes"].includes(part.catchAll)) { error("invalid route parameter flags"); }
        if (!part.hasDefault && part.defaultValue !== undefined) { error("defaultValue without hasDefault"); }
        if (part.catchAll !== "none" && (segment.parts.length !== 1 || i !== plan.segments.length - 1 || part.optional)) { error("invalid catch-all position"); }
        if (part.optional && (part.hasDefault || (segment.parts.length > 1 && (j !== segment.parts.length - 1 || segment.parts[j - 1]?.kind !== "separator")))) { error("invalid optional parameter"); }
        if (segment.parts[j - 1]?.kind === "parameter") { error("adjacent route parameters"); }
        if (matches[0]!.presence === "optional" && !part.optional && !part.hasDefault && part.catchAll === "none") { error("required route value cannot be omitted"); }
      } else if (part.kind === "literal" || part.kind === "separator") {
        if (typeof part.value !== "string" || part.value === "" || /[\\/?#\u0000-\u001f\u007f]/.test(part.value)) { error("invalid route literal"); }
        const next = segment.parts[j + 1];
        if (part.kind === "separator" && (part.value !== "." || j === 0 || j !== segment.parts.length - 2 || next?.kind !== "parameter" || !next.optional)) { error("invalid optional separator"); }
      } else { error("unknown route part"); }
    }
  }
  if (parameters.some(p => p.location === "path" && !used.has(p.id ?? ""))) { error("path parameter is absent from route plan"); }
  if (displayRoute(plan) !== route) { error("display route differs from resolved routePlan"); }
}

/** Canonical display derived solely from the resolved plan; never used as a second execution input. */
export function displayRoute(plan: RoutePlan): string {
  const escape = (s: string): string => s.replaceAll("{", "{{").replaceAll("}", "}}");
  return "/" + plan.segments.map(s => s.parts.map(p => {
    if (p.kind === "literal" || p.kind === "separator") { return escape(p.value); }
    if (p.kind !== "parameter") { return error("unknown route part"); }
    return "{" + (p.catchAll === "none" ? "" : p.catchAll === "encode-slashes" ? "*" : "**") + p.name
      + p.policies.map(v => ":" + escape(v)).join("") + (p.hasDefault ? "=" + escape(p.defaultValue ?? "") : "") + (p.optional ? "?" : "") + "}";
  }).join("")).join("/");
}

function encodeCatchAll(value: string, p: RouteParameter): string {
  if (value.length === 0) { return error("empty route values are distinct from omission", p.parameterId); }
  if (p.catchAll !== "preserve-slashes") { return encodePathSegment(value, p.parameterId); }
  return value.split("/").map(v => v === "" ? "" : encodePathSegment(v, p.parameterId)).join("/");
}

export function buildPlannedUrl(baseUrl: string, plan: RoutePlan, values: ReadonlyMap<string, string>, queryEntries: readonly QueryEntry[]): BuiltUrl {
  const segments: string[] = [];
  let omitted = false;
  const seen = new Set<string>();
  for (const [segmentIndex, segment] of plan.segments.entries()) {
    if (segment.parts.length === 0) { error("empty route segment"); }
    const active: { part: ResolvedRoutePart; value: string; encoded: string }[] = [];
    let omittedSeparator: string | undefined;
    for (const [partIndex, part] of segment.parts.entries()) {
      if (part.kind === "parameter") {
        if (seen.has(part.parameterId)) { error("duplicate route parameter", part.parameterId); }
        seen.add(part.parameterId);
        if (!["none", "encode-slashes", "preserve-slashes"].includes(part.catchAll)) { error("unknown catch-all kind"); }
        if (part.catchAll !== "none" && (segment.parts.length !== 1 || segmentIndex !== plan.segments.length - 1)) { error("catch-all must be the final simple segment"); }
        if (part.optional && part.hasDefault) { error("optional route cannot also have a default"); }
        const value = values.get(part.parameterId);
        if (value === undefined) {
          if (!(part.optional || part.hasDefault || part.catchAll !== "none")) { error("required route value is absent", part.parameterId); }
          if (segment.parts.length > 1) {
            if (partIndex !== segment.parts.length - 1 || active.at(-1)?.part.kind !== "separator") { error("complex omission needs its optional separator"); }
            omittedSeparator = active.pop()!.value;
          }
          continue;
        }
        active.push({ part, value, encoded: encodeCatchAll(value, part) });
      } else if (part.kind === "literal" || part.kind === "separator") {
        if (part.kind === "separator" && (part.value !== "." || segment.parts[partIndex + 1]?.kind !== "parameter")) { error("invalid optional separator"); }
        if (/[\\/?#\u0000-\u001f\u007f]/.test(part.value)) { error("unsafe route literal"); }
        active.push({ part, value: part.value, encoded: encodeQueryComponent(part.value) });
      } else { error("unknown route part"); }
    }
    if (active.length === 0) { omitted = true; continue; }
    if (omitted) { error("omitting an intermediate segment would shift a later value"); }
    if (omittedSeparator !== undefined && active.map(p => p.value).join("").includes(omittedSeparator)) { error("omitted optional part would capture a separator inside its preceding value"); }
    // ASP.NET complex matching searches delimiters from right to left. Verify the recovered values, not just the URL string.
    if (active.length > 1) {
      const combined = active.map(p => p.value).join("");
      let end = combined.length;
      for (let i = active.length - 1; i >= 0; i--) {
        const current = active[i]!;
        if (current.part.kind === "parameter") {
          const delimiter = active[i - 1];
          const begin = delimiter === undefined ? 0 : combined.lastIndexOf(delimiter.value, end - 1) + delimiter.value.length;
          if (begin < 0 || combined.slice(begin, end) !== current.value) { error("complex route value is ambiguous at its separator", current.part.parameterId); }
          end = begin;
        } else {
          if (combined.slice(0, end).endsWith(current.value)) { end -= current.value.length; }
          else { error("complex route literal cannot be matched"); }
        }
      }
      if (end !== 0) { error("complex route values cannot round-trip"); }
    }
    segments.push(active.map(p => p.encoded).join(""));
  }
  for (const id of values.keys()) { if (!seen.has(id)) { error("path parameter is absent from route plan", id); } }
  const base = validateBaseUrl(baseUrl);
  const prefix = base.pathname.replace(/\/$/, "");
  const path = prefix + "/" + segments.join("/");
  const query = queryEntries.map(e => e.value === "" && e.name !== "" ? e.name : e.name + "=" + e.value).join("&");
  const url = new URL(path + (query === "" ? "" : "?" + query), base);
  if (url.origin !== base.origin || (prefix !== "" && url.pathname !== prefix && !url.pathname.startsWith(prefix + "/")) || url.pathname !== path) {
    error("URL normalization left the allowed base path or changed route meaning");
  }
  if (url.search.slice(1) !== query) { error("URL normalization changed query meaning"); }
  return { url, encodedPath: url.pathname, queryEntries };
}
