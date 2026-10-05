import type { JsonValue } from "./ast.js";

/**
 * RFC 8785 JSON Canonicalization Scheme for control documents only (request identity records, envelopes, contract
 * projections). Never applied to business payloads. Numbers are serialized through ECMAScript
 * Number::toString, object members are sorted by UTF-16 code units, lone surrogates and non-finite numbers are errors.
 */
export function canonicalize(value: JsonValue): string {
  const parts: string[] = [];
  write(value, parts);
  return parts.join("");
}

export class JcsError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "JcsError";
  }
}

function compareCodeUnits(a: string, b: string): number {
  const n = Math.min(a.length, b.length);
  for (let i = 0; i < n; i++) {
    const d = a.charCodeAt(i) - b.charCodeAt(i);
    if (d !== 0) {
      return d;
    }
  }
  return a.length - b.length;
}

function write(value: JsonValue, parts: string[]): void {
  switch (value.kind) {
    case "null":
      parts.push("null");
      break;
    case "boolean":
      parts.push(value.value ? "true" : "false");
      break;
    case "number": {
      const n = Number(value.text);
      if (!Number.isFinite(n)) {
        throw new JcsError("NaN and Infinity are not permitted");
      }
      parts.push(String(n)); // ES Number::toString; -0 → "0"
      break;
    }
    case "string":
      parts.push(canonicalString(value.value));
      break;
    case "array": {
      parts.push("[");
      value.items.forEach((item, i) => {
        if (i > 0) {
          parts.push(",");
        }
        write(item, parts);
      });
      parts.push("]");
      break;
    }
    case "object": {
      const entries = [...value.entries].sort((x, y) => compareCodeUnits(x.name, y.name));
      parts.push("{");
      entries.forEach((entry, i) => {
        if (i > 0) {
          parts.push(",");
        }
        parts.push(canonicalString(entry.name), ":");
        write(entry.value, parts);
      });
      parts.push("}");
      break;
    }
  }
}

export function canonicalString(s: string): string {
  let out = '"';
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) {
      const next = i + 1 < s.length ? s.charCodeAt(i + 1) : -1;
      if (next >= 0xdc00 && next <= 0xdfff) {
        out += s[i]! + s[i + 1]!;
        i++;
        continue;
      }
      throw new JcsError("lone surrogate in string");
    }
    if (c >= 0xdc00 && c <= 0xdfff) {
      throw new JcsError("lone surrogate in string");
    }
    switch (c) {
      case 0x08:
        out += "\\b";
        break;
      case 0x09:
        out += "\\t";
        break;
      case 0x0a:
        out += "\\n";
        break;
      case 0x0c:
        out += "\\f";
        break;
      case 0x0d:
        out += "\\r";
        break;
      case 0x22:
        out += '\\"';
        break;
      case 0x5c:
        out += "\\\\";
        break;
      default:
        out += c < 0x20 ? "\\u" + c.toString(16).padStart(4, "0") : s[i]!;
    }
  }
  return out + '"';
}
