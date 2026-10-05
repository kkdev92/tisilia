// The contract's documentation as the page shows it: an entry's summary and text, and for a type the member list its
// description ends with — the heading "**Members**", then a line per member, `- \`name\` — text` (members have no ids an entry could
// target, SV03). The exporter writes that list from [Description] attributes, XML comments and validation attributes.
import type { ContractDocument } from "@kkdev92/tisilia-runtime";
import { documentationOf } from "./explorer.js";

export const membersHeading = "**Members**";

/** What the description of a deprecated operation, parameter or type, or a deprecated member's text, starts with ([Obsolete]). */
export const deprecatedMark = "**Deprecated.**";

export function isDeprecated(text: string | undefined): boolean {
  return text !== undefined && text.trimStart().startsWith(deprecatedMark);
}

export interface Doc {
  readonly summary: string;
  /** The description without the member list. */
  readonly text: string;
  /** Each documented member's text, by the member's name. */
  readonly members: ReadonlyMap<string, string>;
}

const memberLine = /^- `([^`]+)` — (.*)$/;

/**
 * A type's description split into its text and the member list it ends with: the last heading line followed by member lines only.
 * Anything else after the heading (a description using the heading for text of its own) keeps the whole description as text.
 */
export function splitDescription(description: string): { readonly text: string; readonly members: ReadonlyMap<string, string> } {
  const lines = description.replace(/\r\n?/g, "\n").split("\n");
  const heading = lines.map((l) => l.trim()).lastIndexOf(membersHeading);
  const members = new Map<string, string>();
  for (const line of heading < 0 ? [] : lines.slice(heading + 1).filter((l) => l.trim() !== "")) {
    const m = memberLine.exec(line);
    if (m === null) {
      members.clear();
      break;
    }
    if (!members.has(m[1]!)) {
      members.set(m[1]!, m[2]!.trim());
    }
  }
  return members.size === 0 ? { text: description.trim(), members } : { text: lines.slice(0, heading).join("\n").trim(), members };
}

const cache = new WeakMap<ContractDocument, Map<string, Doc | null>>();

/** The documentation of an operation, parameter, response case or type, split for display; undefined when there is none. */
export function docOf(document: ContractDocument, id: string | undefined): Doc | undefined {
  if (id === undefined) {
    return undefined;
  }
  let docs = cache.get(document);
  if (docs === undefined) {
    docs = new Map();
    cache.set(document, docs);
  }
  let doc = docs.get(id);
  if (doc === undefined) {
    const entry = documentationOf(document, id);
    doc = entry === undefined ? null : { summary: entry.summary, ...splitDescription(entry.description) };
    docs.set(id, doc);
  }
  return doc ?? undefined;
}
