import { CodecError } from "../codec/errors.js";

/** True when the string contains no lone surrogate code units (the default string domain). */
export function isWellFormedUnicode(s: string): boolean {
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    if (c >= 0xd800 && c <= 0xdbff) {
      const next = i + 1 < s.length ? s.charCodeAt(i + 1) : -1;
      if (next < 0xdc00 || next > 0xdfff) {
        return false;
      }
      i++;
    } else if (c >= 0xdc00 && c <= 0xdfff) {
      return false;
    }
  }
  return true;
}

export function validateString(value: unknown, path: string): string {
  if (typeof value !== "string") {
    throw new CodecError("type-mismatch", path, "string required");
  }
  if (!isWellFormedUnicode(value)) {
    throw new CodecError("domain-rule", path, "string contains a lone surrogate; the default string profile requires well-formed Unicode");
  }
  return value;
}

/** System.Char: exactly one UTF-16 code unit that is not a surrogate. */
export function validateChar(value: unknown, path: string): string {
  if (typeof value !== "string" || value.length !== 1) {
    throw new CodecError("type-mismatch", path, "char requires exactly one UTF-16 code unit");
  }
  const c = value.charCodeAt(0);
  if (c >= 0xd800 && c <= 0xdfff) {
    throw new CodecError("domain-rule", path, "char cannot be a surrogate code unit");
  }
  return value;
}

export function utf16Length(s: string): number {
  return s.length;
}

export function checkUtf16Length(value: string, min: number | undefined, max: number | undefined, path: string): void {
  if (min !== undefined && value.length < min) {
    throw new CodecError("range", path, `string shorter than ${min} UTF-16 code units`);
  }
  if (max !== undefined && value.length > max) {
    throw new CodecError("range", path, `string longer than ${max} UTF-16 code units`);
  }
}

declare const brand: unique symbol;

/** Canonical lowercase "D" form `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`. */
export type Guid = string & { readonly [brand]: "Guid" };

const guidD = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

/** Parses the D format only (System.Text.Json reads only this form; observed: braces / no hyphens rejected), normalizing case. */
export function parseGuid(text: string, path: string): Guid {
  if (!guidD.test(text)) {
    throw new CodecError("grammar", path, "guid must be in 8-4-4-4-12 hexadecimal form");
  }
  return text.toLowerCase() as Guid;
}

export function guid(text: string): Guid {
  return parseGuid(text, "");
}

export function validateGuid(value: unknown, path: string): Guid {
  if (typeof value !== "string") {
    throw new CodecError("type-mismatch", path, "guid requires a string");
  }
  return parseGuid(value, path);
}

export function formatGuid(value: Guid): string {
  return value;
}

/** Bytes are Uint8Array; the wire form is RFC 4648 §4 base64 with mandatory padding and canonical trailing bits. */
const base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const base64Lookup = new Int16Array(128).fill(-1);
for (let i = 0; i < base64Alphabet.length; i++) {
  base64Lookup[base64Alphabet.charCodeAt(i)] = i;
}

export function encodeBase64(bytes: Uint8Array): string {
  let out = "";
  let i = 0;
  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8) | bytes[i + 2]!;
    out += base64Alphabet[n >> 18]! + base64Alphabet[(n >> 12) & 63]! + base64Alphabet[(n >> 6) & 63]! + base64Alphabet[n & 63]!;
  }
  const rest = bytes.length - i;
  if (rest === 1) {
    const n = bytes[i]! << 16;
    out += base64Alphabet[n >> 18]! + base64Alphabet[(n >> 12) & 63]! + "==";
  } else if (rest === 2) {
    const n = (bytes[i]! << 16) | (bytes[i + 1]! << 8);
    out += base64Alphabet[n >> 18]! + base64Alphabet[(n >> 12) & 63]! + base64Alphabet[(n >> 6) & 63]! + "=";
  }
  return out;
}

/**
 * Strict decoder: standard alphabet, length multiple of 4, padding required, no whitespace, non-zero trailing bits
 * rejected (observed on .NET 10: "/wA" and "/wB=" rejected, "/w A=" accepted — the whitespace alias is not canonical).
 */
export function decodeBase64(text: string, path: string): Uint8Array {
  if (text.length % 4 !== 0) {
    throw new CodecError("grammar", path, "base64 length must be a multiple of 4 (padding required)");
  }
  if (text.length === 0) {
    return new Uint8Array(0);
  }
  let padding = 0;
  if (text.endsWith("==")) {
    padding = 2;
  } else if (text.endsWith("=")) {
    padding = 1;
  }
  const out = new Uint8Array((text.length / 4) * 3 - padding);
  let o = 0;
  for (let i = 0; i < text.length; i += 4) {
    const vals: number[] = [];
    for (let k = 0; k < 4; k++) {
      const ch = text.charCodeAt(i + k);
      const isPad = ch === 0x3d;
      if (isPad) {
        if (i + 4 < text.length || k < 4 - padding) {
          throw new CodecError("grammar", path, "misplaced base64 padding");
        }
        vals.push(0);
        continue;
      }
      const v = ch < 128 ? base64Lookup[ch]! : -1;
      if (v < 0) {
        throw new CodecError("grammar", path, "invalid base64 character");
      }
      vals.push(v);
    }
    const n = (vals[0]! << 18) | (vals[1]! << 12) | (vals[2]! << 6) | vals[3]!;
    const isLast = i + 4 === text.length;
    out[o++] = (n >> 16) & 255;
    if (!isLast || padding < 2) {
      out[o++] = (n >> 8) & 255;
    }
    if (!isLast || padding < 1) {
      out[o++] = n & 255;
    }
    if (isLast) {
      const trailingBits = padding === 2 ? n & 0xffff : padding === 1 ? n & 0xff : 0;
      if (trailingBits !== 0) {
        throw new CodecError("grammar", path, "base64 has non-zero trailing bits (non-canonical encoding)");
      }
    }
  }
  return out;
}

export function validateBytes(value: unknown, path: string): Uint8Array {
  if (!(value instanceof Uint8Array)) {
    throw new CodecError("type-mismatch", path, "bytes require a Uint8Array");
  }
  return value;
}
