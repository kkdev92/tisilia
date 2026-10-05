import { type JsonValue, type JsonEntry, jsonNull } from "./ast.js";
import { resolveLimits, type Limits } from "./limits.js";

/**
 * Failure raised by the lossless parser. `code` is a fixed, safe identifier; `offset` is the UTF-16 index (or byte
 * offset for UTF-8 failures). The offending token text is never copied into the message.
 */
export class JsonParseError extends Error {
  constructor(
    readonly code: JsonParseErrorCode,
    readonly offset: number,
    message: string,
  ) {
    super(message);
    this.name = "JsonParseError";
  }
}

export type JsonParseErrorCode =
  | "invalid-utf8"
  | "bom"
  | "unexpected-end"
  | "unexpected-token"
  | "invalid-number"
  | "invalid-string"
  | "invalid-escape"
  | "control-character"
  | "depth-limit"
  | "token-limit"
  | "number-length-limit"
  | "trailing-content";

export interface ParseOptions {
  readonly limits?: Partial<Limits>;
  /** Cooperative cancellation check invoked periodically. Throw to abort. */
  readonly checkpoint?: () => void;
}

let cachedDecoder: TextDecoder | undefined;

/** Strict UTF-8 decoding: invalid sequences and a leading BOM are rejected. */
export function decodeUtf8Strict(bytes: Uint8Array): string {
  cachedDecoder ??= new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
  let text: string;
  try {
    text = cachedDecoder.decode(bytes);
  } catch {
    throw new JsonParseError("invalid-utf8", 0, "body is not valid UTF-8");
  }
  if (text.length > 0 && text.charCodeAt(0) === 0xfeff) {
    throw new JsonParseError("bom", 0, "UTF-8 byte order mark is not accepted");
  }
  return text;
}

export function parseJsonBytes(bytes: Uint8Array, options: ParseOptions = {}): JsonValue {
  return parseJson(decodeUtf8Strict(bytes), options);
}

/** Parses text into the lossless AST enforcing depth / token / number-length limits. */
export function parseJson(text: string, options: ParseOptions = {}): JsonValue {
  const limits: Limits = resolveLimits(options.limits);
  const parser = new Parser(text, limits, options.checkpoint);
  parser.skipWhitespace();
  const value = parser.parseValue(0);
  parser.skipWhitespace();
  if (parser.pos !== text.length) {
    throw new JsonParseError("trailing-content", parser.pos, "unexpected content after the JSON value");
  }
  return value;
}

const CH_SPACE = 0x20;
const CH_TAB = 0x09;
const CH_LF = 0x0a;
const CH_CR = 0x0d;

class Parser {
  pos = 0;
  private tokens = 0;

  constructor(
    private readonly text: string,
    private readonly limits: Limits,
    private readonly checkpoint: (() => void) | undefined,
  ) {}

  private countToken(): void {
    this.tokens++;
    if (this.tokens > this.limits.maxTokens) {
      throw new JsonParseError("token-limit", this.pos, "token limit exceeded");
    }
    if ((this.tokens & 0x3ff) === 0) {
      this.checkpoint?.();
    }
  }

  skipWhitespace(): void {
    const t = this.text;
    let p = this.pos;
    while (p < t.length) {
      const c = t.charCodeAt(p);
      if (c === CH_SPACE || c === CH_TAB || c === CH_LF || c === CH_CR) {
        p++;
      } else {
        break;
      }
    }
    this.pos = p;
  }

  parseValue(depth: number): JsonValue {
    const t = this.text;
    if (this.pos >= t.length) {
      throw new JsonParseError("unexpected-end", this.pos, "unexpected end of JSON");
    }
    this.countToken();
    const c = t.charCodeAt(this.pos);
    switch (c) {
      case 0x7b: // {
        return this.parseObject(depth + 1);
      case 0x5b: // [
        return this.parseArray(depth + 1);
      case 0x22: // "
        return { kind: "string", value: this.parseString() };
      case 0x74: // t
        this.expectLiteral("true");
        return { kind: "boolean", value: true };
      case 0x66: // f
        this.expectLiteral("false");
        return { kind: "boolean", value: false };
      case 0x6e: // n
        this.expectLiteral("null");
        return jsonNull;
      default:
        if (c === 0x2d || (c >= 0x30 && c <= 0x39)) {
          return { kind: "number", text: this.parseNumber() };
        }
        throw new JsonParseError("unexpected-token", this.pos, "unexpected character");
    }
  }

  private expectLiteral(literal: string): void {
    if (!this.text.startsWith(literal, this.pos)) {
      throw new JsonParseError("unexpected-token", this.pos, "invalid literal");
    }
    this.pos += literal.length;
  }

  private parseObject(depth: number): JsonValue {
    if (depth > this.limits.maxDepth) {
      throw new JsonParseError("depth-limit", this.pos, "depth limit exceeded");
    }
    this.pos++; // {
    const entries: JsonEntry[] = [];
    this.skipWhitespace();
    if (this.peek() === 0x7d) {
      this.pos++;
      return { kind: "object", entries };
    }
    for (;;) {
      this.skipWhitespace();
      if (this.peek() !== 0x22) {
        throw new JsonParseError("unexpected-token", this.pos, "expected property name");
      }
      this.countToken();
      const name = this.parseString();
      this.skipWhitespace();
      if (this.peek() !== 0x3a) {
        throw new JsonParseError("unexpected-token", this.pos, "expected ':'");
      }
      this.pos++;
      this.skipWhitespace();
      const value = this.parseValue(depth);
      entries.push({ name, value });
      this.skipWhitespace();
      const c = this.peek();
      if (c === 0x2c) {
        this.pos++;
        continue;
      }
      if (c === 0x7d) {
        this.pos++;
        return { kind: "object", entries };
      }
      throw new JsonParseError(this.pos >= this.text.length ? "unexpected-end" : "unexpected-token", this.pos, "expected ',' or '}'");
    }
  }

  private parseArray(depth: number): JsonValue {
    if (depth > this.limits.maxDepth) {
      throw new JsonParseError("depth-limit", this.pos, "depth limit exceeded");
    }
    this.pos++; // [
    const items: JsonValue[] = [];
    this.skipWhitespace();
    if (this.peek() === 0x5d) {
      this.pos++;
      return { kind: "array", items };
    }
    for (;;) {
      this.skipWhitespace();
      items.push(this.parseValue(depth));
      this.skipWhitespace();
      const c = this.peek();
      if (c === 0x2c) {
        this.pos++;
        continue;
      }
      if (c === 0x5d) {
        this.pos++;
        return { kind: "array", items };
      }
      throw new JsonParseError(this.pos >= this.text.length ? "unexpected-end" : "unexpected-token", this.pos, "expected ',' or ']'");
    }
  }

  private peek(): number {
    return this.pos < this.text.length ? this.text.charCodeAt(this.pos) : -1;
  }

  private parseNumber(): string {
    const t = this.text;
    const start = this.pos;
    let p = start;
    if (t.charCodeAt(p) === 0x2d) {
      p++;
    }
    if (p >= t.length) {
      throw new JsonParseError("invalid-number", p, "expected digit");
    }
    const first = t.charCodeAt(p);
    if (first === 0x30) {
      p++;
    } else if (first >= 0x31 && first <= 0x39) {
      while (p < t.length && isDigit(t.charCodeAt(p))) {
        p++;
      }
    } else {
      throw new JsonParseError("invalid-number", p, "expected digit");
    }
    if (p < t.length && t.charCodeAt(p) === 0x2e) {
      p++;
      const fracStart = p;
      while (p < t.length && isDigit(t.charCodeAt(p))) {
        p++;
      }
      if (p === fracStart) {
        throw new JsonParseError("invalid-number", p, "expected fraction digit");
      }
    }
    if (p < t.length && (t.charCodeAt(p) === 0x65 || t.charCodeAt(p) === 0x45)) {
      p++;
      if (p < t.length && (t.charCodeAt(p) === 0x2b || t.charCodeAt(p) === 0x2d)) {
        p++;
      }
      const expStart = p;
      while (p < t.length && isDigit(t.charCodeAt(p))) {
        p++;
      }
      if (p === expStart) {
        throw new JsonParseError("invalid-number", p, "expected exponent digit");
      }
    }
    if (p - start > this.limits.maxNumberCharacters) {
      throw new JsonParseError("number-length-limit", start, "number lexeme exceeds maxNumberCharacters");
    }
    this.pos = p;
    return t.slice(start, p);
  }

  private parseString(): string {
    const t = this.text;
    let p = this.pos + 1; // opening quote
    let out = "";
    let segmentStart = p;
    for (;;) {
      if (p >= t.length) {
        throw new JsonParseError("unexpected-end", p, "unterminated string");
      }
      const c = t.charCodeAt(p);
      if (c === 0x22) {
        out += t.slice(segmentStart, p);
        this.pos = p + 1;
        return out;
      }
      if (c < 0x20) {
        throw new JsonParseError("control-character", p, "control character in string must be escaped");
      }
      if (c === 0x5c) {
        out += t.slice(segmentStart, p);
        p++;
        if (p >= t.length) {
          throw new JsonParseError("unexpected-end", p, "unterminated escape");
        }
        const e = t.charCodeAt(p);
        switch (e) {
          case 0x22:
            out += '"';
            break;
          case 0x5c:
            out += "\\";
            break;
          case 0x2f:
            out += "/";
            break;
          case 0x62:
            out += "\b";
            break;
          case 0x66:
            out += "\f";
            break;
          case 0x6e:
            out += "\n";
            break;
          case 0x72:
            out += "\r";
            break;
          case 0x74:
            out += "\t";
            break;
          case 0x75: {
            if (p + 4 >= t.length) {
              throw new JsonParseError("invalid-escape", p, "truncated \\u escape");
            }
            const hex = t.slice(p + 1, p + 5);
            if (!/^[0-9a-fA-F]{4}$/.test(hex)) {
              throw new JsonParseError("invalid-escape", p, "invalid \\u escape");
            }
            // Escaped surrogate code units are preserved as-is; codecs enforce well-formedness.
            out += String.fromCharCode(parseInt(hex, 16));
            p += 4;
            break;
          }
          default:
            throw new JsonParseError("invalid-escape", p, "invalid escape");
        }
        p++;
        segmentStart = p;
        continue;
      }
      p++;
    }
  }
}

function isDigit(c: number): boolean {
  return c >= 0x30 && c <= 0x39;
}
