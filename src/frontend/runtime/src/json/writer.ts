import type { JsonValue } from "./ast.js";

/**
 * Serializes an AST to compact RFC 8259 text. Only the escapes required by the grammar are emitted (`"`, `\`, and
 * U+0000–U+001F); non-ASCII is written raw and the transport encodes UTF-8. Lone surrogates are escaped as `\uXXXX`
 * so that the output stays valid UTF-8 (no content normalization).
 */
export function writeJson(value: JsonValue): string {
  const parts: string[] = [];
  write(value, parts);
  return parts.join("");
}

let cachedEncoder: TextEncoder | undefined;

export function writeJsonBytes(value: JsonValue): Uint8Array {
  cachedEncoder ??= new TextEncoder();
  return cachedEncoder.encode(writeJson(value));
}

function write(value: JsonValue, parts: string[]): void {
  switch (value.kind) {
    case "null":
      parts.push("null");
      break;
    case "boolean":
      parts.push(value.value ? "true" : "false");
      break;
    case "number":
      parts.push(value.text);
      break;
    case "string":
      parts.push(quote(value.value));
      break;
    case "array": {
      parts.push("[");
      for (let i = 0; i < value.items.length; i++) {
        if (i > 0) {
          parts.push(",");
        }
        write(value.items[i]!, parts);
      }
      parts.push("]");
      break;
    }
    case "object": {
      parts.push("{");
      for (let i = 0; i < value.entries.length; i++) {
        if (i > 0) {
          parts.push(",");
        }
        const entry = value.entries[i]!;
        parts.push(quote(entry.name), ":");
        write(entry.value, parts);
      }
      parts.push("}");
      break;
    }
  }
}

export function quote(s: string): string {
  let out = '"';
  let start = 0;
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    let esc: string | undefined;
    if (c === 0x22) {
      esc = '\\"';
    } else if (c === 0x5c) {
      esc = "\\\\";
    } else if (c < 0x20) {
      switch (c) {
        case 0x08:
          esc = "\\b";
          break;
        case 0x09:
          esc = "\\t";
          break;
        case 0x0a:
          esc = "\\n";
          break;
        case 0x0c:
          esc = "\\f";
          break;
        case 0x0d:
          esc = "\\r";
          break;
        default:
          esc = "\\u" + c.toString(16).padStart(4, "0");
      }
    } else if (c >= 0xd800 && c <= 0xdfff) {
      const isHigh = c <= 0xdbff;
      const next = i + 1 < s.length ? s.charCodeAt(i + 1) : -1;
      if (isHigh && next >= 0xdc00 && next <= 0xdfff) {
        i++; // valid pair, keep raw
        continue;
      }
      esc = "\\u" + c.toString(16).padStart(4, "0");
    }
    if (esc !== undefined) {
      out += s.slice(start, i) + esc;
      start = i + 1;
    }
  }
  return out + s.slice(start) + '"';
}
