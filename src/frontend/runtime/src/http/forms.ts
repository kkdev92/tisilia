import type { CodecContext } from "../codec/abi.js";
import { CodecError } from "../codec/errors.js";
import type { ScalarName } from "../codec/scalars.js";
import { ordinalUpper } from "../primitives/ordinalCasing.js";
import { sha256 } from "../sha256.js";
import { formatScalarForBinding, formatScalarForRequestCulture } from "./binders.js";
import { encodeQueryComponent } from "./url.js";
import { parseMediaType } from "./mediaType.js";

/** A finite file part. Bytes are copied while preparing the request, before awaiting credentials. */
export interface UploadFile {
  readonly bytes: Uint8Array;
  readonly fileName: string;
  readonly contentType?: string;
}

export interface FormFieldDescriptor {
  readonly name: string;
  /** `map`: a ReadonlyMap whose entries are written as `name[key]` (a root map as `[key]`), with the field's value scalar or format. */
  readonly kind: "value" | "file" | "object" | "map";
  /** The scalar of a map's keys (a string, an integer or a Guid), written as its invariant canonical text. */
  readonly key?: ScalarName;
  readonly fields?: readonly FormFieldDescriptor[];
  readonly wireName?: "";
  readonly scalar?: ScalarName;
  readonly repeated: boolean;
  readonly rejectBlank?: boolean;
  readonly indexed?: boolean;
  /** The server reads the value with the request culture (MVC): the scalar is written so that every culture reads it the same way. */
  readonly requestCulture?: boolean;
  readonly format?: (value: unknown, context: CodecContext) => string;
  readonly presence: "required" | "optional";
}

export class FormLimitError extends CodecError {
  constructor() { super("limit", "/body", "encoded form exceeds maxBodyBytes"); }
}

function safeName(value: unknown, path: string, file: boolean): string {
  if (typeof value !== "string" || value.length === 0 || /[\x00-\x1f\x7f"\\]/.test(value) || (file && value.includes("/"))) {
    throw new CodecError("grammar", path, "form names must be non-empty and contain no control, quote, backslash or filename path separator");
  }
  encodeQueryComponent(value); // Reject malformed Unicode instead of replacing it during UTF-8 encoding.
  return value;
}

export function encodeForm(fields: readonly FormFieldDescriptor[], mediaType: string, raw: unknown, context: CodecContext): { bytes: Uint8Array; contentType: string; text?: string } {
  const encoder = new TextEncoder();
  const multipart = mediaType === "multipart/form-data";
  const parts: { headers: string; bytes: Uint8Array }[] = [];
  const pairs: string[] = [];
  let size = 0;
  const reserve = (length: number): void => { size += length; if (size > context.limits.maxBodyBytes) { throw new FormLimitError(); } };
  const textBytes = (text: string): Uint8Array => {
    if (text.length > context.limits.maxBodyBytes) { throw new FormLimitError(); }
    const bytes = encoder.encode(text); reserve(bytes.byteLength); return bytes;
  };
  let emitted = 0;
  const wireOwners = new Map<string, string>();
  const walk = (fields: readonly FormFieldDescriptor[], raw: unknown, prefix: string, context: CodecContext, depth: number): void => {
    if (depth > Math.min(16, context.limits.maxDepth)) { throw new CodecError("limit", context.path, "form exceeds maximum nesting"); }
    if (raw === null || typeof raw !== "object" || Array.isArray(raw)) { throw new CodecError("type-mismatch", context.path, "form body requires an object"); }
    const body = raw as Record<string, unknown>;
    if (Object.keys(body).some(name => !fields.some(f => f.name === name))) { throw new CodecError("unsupported", context.path, "form contains an undeclared field"); }
    if (new Set(fields.map(f => ordinalUpper(f.name))).size !== fields.length) { throw new CodecError("unsupported", context.path, "form has overlapping field names"); }
    for (const field of fields) {
      context.checkpoint();
      const ctx = context.child(field.name);
      const key = safeName(field.name, ctx.path, false);
      if (field.wireName !== undefined && (field.wireName !== "" || depth !== 0 || (field.indexed !== true && field.kind !== "map"))) { throw new CodecError("unsupported", ctx.path, "invalid root collection wire name"); }
      const name = prefix + (field.wireName ?? key);
      const value = Object.hasOwn(body, key) ? body[key] : undefined;
      if (value === undefined) {
        if (field.presence === "required") { throw new CodecError("missing-required", ctx.path, "form field is required"); }
        continue;
      }
      if (field.repeated && !Array.isArray(value)) { throw new CodecError("type-mismatch", ctx.path, "repeated form field requires an array"); }
      if (field.indexed === true && (!field.repeated || (value as unknown[]).length === 0)) { throw new CodecError("grammar", ctx.path, "indexed form collections require at least one item; omit an optional collection instead"); }
      // each item with its wire name, and the path that owns that name (a repeated name has one owner; each map key owns its own)
      const items: { readonly item: unknown; readonly wireName: string; readonly itemContext: CodecContext; readonly owner: string }[] = [];
      if (field.kind === "map") {
        if (!(value instanceof Map)) { throw new CodecError("type-mismatch", ctx.path, "form dictionary requires a Map"); }
        if (value.size === 0) { throw new CodecError("grammar", ctx.path, "form dictionaries require at least one entry; omit an optional dictionary instead"); }
        const keys = new Set<string>();
        for (const [key, item] of value as ReadonlyMap<unknown, unknown>) {
          context.checkpoint();
          if (field.key === undefined) { throw new CodecError("unsupported", ctx.path, "form dictionary key scalar is not declared"); }
          const keyText = formatScalarForBinding(field.key, key, ctx.path);
          const itemContext = ctx.child(keyText);
          // ASP.NET Core reads a key up to its first ']' and gathers keys ignoring case, merging the values of keys that differ only in case
          if (keyText.includes("]")) { throw new CodecError("grammar", itemContext.path, "form dictionary keys cannot contain ']'"); }
          if (keys.size === keys.add(ordinalUpper(keyText)).size) { throw new CodecError("grammar", itemContext.path, "form dictionary keys must differ ignoring case"); }
          const wireName = `${name}[${keyText}]`;
          // a multipart name carries the key: no quote, backslash or control character (an empty key still names Labels[])
          if (multipart) { safeName(wireName, itemContext.path, false); }
          items.push({ item, wireName, itemContext, owner: itemContext.path });
        }
      } else {
        let itemIndex = 0;
        for (const item of field.repeated ? value as unknown[] : [value]) {
          items.push({ item, wireName: field.indexed === true ? `${name}[${itemIndex}]` : name, itemContext: field.repeated ? ctx.child(itemIndex) : ctx, owner: ctx.path });
          itemIndex++;
        }
      }
      for (const { item, wireName, itemContext, owner: itemOwner } of items) {
        context.checkpoint();
        if (field.kind === "object") {
          if (field.fields === undefined || field.repeated !== (field.indexed === true)) { throw new CodecError("unsupported", ctx.path, "object form fields require children and indexed repetition"); }
          const before = emitted;
          walk(field.fields, item, wireName + ".", itemContext, depth + 1);
          if (emitted === before) { throw new CodecError("grammar", itemContext.path, "form object must emit at least one field; empty items would truncate the collection"); }
          continue;
        }
        const wireKey = ordinalUpper(wireName);
        const owner = wireOwners.get(wireKey);
        if (owner !== undefined && owner !== itemOwner) { throw new CodecError("unsupported", itemContext.path, "form has overlapping wire names"); }
        wireOwners.set(wireKey, itemOwner);
        emitted++;
        let bytes: Uint8Array;
        let headers = `Content-Disposition: form-data; name="${wireName}"`;
        if (field.kind === "file") {
          if (!multipart || item === null || typeof item !== "object" || !((item as UploadFile).bytes instanceof Uint8Array)) {
            throw new CodecError("type-mismatch", itemContext.path, "multipart file requires bytes and fileName");
          }
          const file = item as UploadFile;
          const fileName = safeName(file.fileName, itemContext.path, true);
          const type = file.contentType ?? "application/octet-stream";
          if (typeof type !== "string") { throw new CodecError("type-mismatch", itemContext.path, "file contentType must be a string"); }
          const media = parseMediaType(type);
          if (media === undefined || media.essence !== type.toLowerCase() || type.includes("*")) { throw new CodecError("grammar", itemContext.path, "file contentType requires a concrete media type without parameters"); }
          reserve(file.bytes.byteLength);
          bytes = new Uint8Array(file.bytes);
          headers += `; filename="${fileName}"\r\nContent-Type: ${type}`;
        } else {
          if (field.scalar === undefined && field.format === undefined) { throw new CodecError("unsupported", itemContext.path, "form scalar is not declared"); }
          const text = field.format !== undefined ? field.format(item, itemContext)
            : (field.requestCulture === true ? formatScalarForRequestCulture : formatScalarForBinding)(field.scalar!, item, itemContext.path);
          // binder text is never empty, as for parameters: ASP.NET Core skips an empty item of an optional array element type
          if (field.format !== undefined && text.length === 0) { throw new CodecError("grammar", itemContext.path, "this form value cannot be empty"); }
          if (text.length > context.limits.maxBodyBytes) { throw new FormLimitError(); }
          // String.IsNullOrWhiteSpace includes NEL and excludes BOM, unlike JavaScript trim().
          if (field.rejectBlank === true && /^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]*$/.test(text)) { throw new CodecError("grammar", itemContext.path, "this form binder converts blank strings to null; omit optional fields instead"); }
          encodeQueryComponent(text);
          if (!multipart) {
            const pair = `${encodeQueryComponent(wireName)}=${encodeQueryComponent(text)}`;
            reserve(pair.length + (pairs.length > 0 ? 1 : 0)); pairs.push(pair); continue;
          }
          bytes = textBytes(text);
        }
        reserve(encoder.encode(headers).byteLength + 4);
        parts.push({ headers, bytes });
      }
    }
  };
  walk(fields, raw, "", context, 0);
  if (!multipart) { const text = pairs.join("&"); return { bytes: encoder.encode(text), contentType: mediaType, text }; }
  // Stable boundary keeps request previews, SSR identities and the sent bytes identical. Verify no delimiter collision.
  const payload = new Uint8Array(parts.reduce((n, p) => n + p.bytes.length, 0));
  let offset = 0;
  for (const part of parts) { payload.set(part.bytes, offset); offset += part.bytes.length; }
  const boundary = "tisilia-" + Array.from(sha256(payload).subarray(0, 24), b => b.toString(16).padStart(2, "0")).join("");
  const marker = encoder.encode(boundary);
  for (const part of parts) {
    for (let i = 0; i <= part.bytes.length - marker.length; i++) {
      if (part.bytes[i] === marker[0] && marker.every((b, j) => part.bytes[i + j] === b)) { throw new CodecError("grammar", context.path, "multipart boundary collides with file content"); }
    }
  }
  const chunks: Uint8Array[] = [];
  // Headers and payload have already been accounted for; add only delimiter overhead here.
  for (const part of parts) { reserve(boundary.length + 6); chunks.push(encoder.encode(`--${boundary}\r\n${part.headers}\r\n\r\n`), part.bytes, encoder.encode("\r\n")); }
  reserve(boundary.length + 6); chunks.push(encoder.encode(`--${boundary}--\r\n`));
  const bytes = new Uint8Array(size); offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  return { bytes, contentType: `multipart/form-data; boundary=${boundary}` };
}
