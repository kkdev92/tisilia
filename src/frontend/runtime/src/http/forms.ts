import type { CodecContext } from "../codec/abi.js";
import { CodecError } from "../codec/errors.js";
import type { ScalarName } from "../codec/scalars.js";
import { ordinalUpper } from "../primitives/ordinalCasing.js";
import { sha256 } from "../sha256.js";
import { formatScalarForBinding } from "./binders.js";
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
  readonly kind: "value" | "file" | "object";
  readonly fields?: readonly FormFieldDescriptor[];
  readonly wireName?: "";
  readonly scalar?: ScalarName;
  readonly repeated: boolean;
  readonly rejectBlank?: boolean;
  readonly indexed?: boolean;
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
      if (field.wireName !== undefined && (field.wireName !== "" || depth !== 0 || field.indexed !== true)) { throw new CodecError("unsupported", ctx.path, "invalid root collection wire name"); }
      const name = prefix + (field.wireName ?? key);
      const value = Object.hasOwn(body, key) ? body[key] : undefined;
      if (value === undefined) {
        if (field.presence === "required") { throw new CodecError("missing-required", ctx.path, "form field is required"); }
        continue;
      }
      if (field.repeated && !Array.isArray(value)) { throw new CodecError("type-mismatch", ctx.path, "repeated form field requires an array"); }
      if (field.indexed === true && (!field.repeated || (value as unknown[]).length === 0)) { throw new CodecError("grammar", ctx.path, "indexed form collections require at least one item; omit an optional collection instead"); }
      let itemIndex = 0;
      for (const item of field.repeated ? value as unknown[] : [value]) {
        context.checkpoint();
        const wireName = field.indexed === true ? `${name}[${itemIndex}]` : name;
        const itemContext = field.repeated ? ctx.child(itemIndex) : ctx;
        itemIndex++;
        if (field.kind === "object") {
          if (field.fields === undefined || field.repeated !== (field.indexed === true)) { throw new CodecError("unsupported", ctx.path, "object form fields require children and indexed repetition"); }
          const before = emitted;
          walk(field.fields, item, wireName + ".", itemContext, depth + 1);
          if (emitted === before) { throw new CodecError("grammar", itemContext.path, "form object must emit at least one field; empty items would truncate the collection"); }
          continue;
        }
        const wireKey = ordinalUpper(wireName);
        const owner = wireOwners.get(wireKey);
        if (owner !== undefined && owner !== ctx.path) { throw new CodecError("unsupported", itemContext.path, "form has overlapping wire names"); }
        wireOwners.set(wireKey, ctx.path);
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
          const text = field.format === undefined ? formatScalarForBinding(field.scalar!, item, itemContext.path) : field.format(item, itemContext);
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
