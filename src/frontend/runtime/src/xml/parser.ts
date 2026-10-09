import { resolveLimits, type Limits } from "../json/limits.js";
import { isNcName, isXmlSpace, xmlNamespace, xmlnsNamespace, type XmlAttribute, type XmlChild, type XmlElement } from "./dom.js";

/** Failure raised by the XML parser. `code` is a fixed, safe identifier; the document's text is never copied into the message. */
export class XmlParseError extends Error {
  constructor(
    readonly code: XmlParseErrorCode,
    readonly offset: number,
    message: string,
  ) {
    super(message);
    this.name = "XmlParseError";
  }
}

export type XmlParseErrorCode =
  | "invalid-utf8"
  | "encoding"
  | "unexpected-end"
  | "malformed"
  | "character"
  | "doctype"
  | "processing-instruction"
  | "entity"
  | "namespace"
  | "duplicate-attribute"
  | "depth-limit"
  | "token-limit"
  | "trailing-content";

export interface XmlParseOptions {
  readonly limits?: Partial<Limits>;
  /** Cooperative cancellation check invoked periodically. Throw to abort. */
  readonly checkpoint?: () => void;
}

let cachedDecoder: TextDecoder | undefined;

/** Parses a UTF-8 document (strict decoding; a byte order mark is allowed, as XML allows it). */
export function parseXmlBytes(bytes: Uint8Array, options: XmlParseOptions = {}): XmlElement {
  cachedDecoder ??= new TextDecoder("utf-8", { fatal: true, ignoreBOM: false });
  let text: string;
  try {
    text = cachedDecoder.decode(bytes);
  } catch {
    throw new XmlParseError("invalid-utf8", 0, "body is not valid UTF-8");
  }
  return parseXml(text, options);
}

/**
 * Parses an XML 1.0 document with namespaces and returns its root element. A document type declaration, processing instructions
 * other than the XML declaration, entities other than the five predefined ones, and an encoding other than UTF-8 are refused (MVC's
 * XmlSerializer formatters write none of them, and their reader refuses the first three).
 */
export function parseXml(text: string, options: XmlParseOptions = {}): XmlElement {
  const limits = resolveLimits(options.limits);
  // end-of-line handling (XML 1.0 §2.11): a carriage return, alone or before a line feed, is a line feed; a character reference keeps it
  const source = text.replace(/\r\n?/g, "\n");
  const parser = new Parser(source, limits, options.checkpoint);
  return parser.document();
}

const LT = 0x3c;
const GT = 0x3e;
const AMP = 0x26;
const SLASH = 0x2f;
const EQ = 0x3d;
const QUOT = 0x22;
const APOS = 0x27;
const QUESTION = 0x3f;
const BANG = 0x21;

const predefined: Readonly<Record<string, string>> = { amp: "&", lt: "<", gt: ">", quot: "\"", apos: "'" };

class Parser {
  private pos = 0;
  private tokens = 0;

  constructor(
    private readonly text: string,
    private readonly limits: Limits,
    private readonly checkpoint: (() => void) | undefined,
  ) {}

  document(): XmlElement {
    if (this.text.charCodeAt(0) === 0xfeff) {
      this.pos = 1;
    }
    if (this.text.startsWith("<?xml", this.pos) && isXmlSpace(this.text.charCodeAt(this.pos + 5))) {
      this.declaration();
    }
    this.misc();
    if (this.pos >= this.text.length) {
      throw new XmlParseError("unexpected-end", this.pos, "the document has no root element");
    }
    if (this.text.charCodeAt(this.pos) !== LT) {
      throw new XmlParseError("malformed", this.pos, "text before the root element");
    }
    const root = this.element(new Map([["xml", xmlNamespace]]), "", 1);
    this.misc();
    if (this.pos !== this.text.length) {
      throw new XmlParseError("trailing-content", this.pos, "content after the root element");
    }
    return root;
  }

  /** `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>`: the version is 1.0 and the encoding, when named, UTF-8. */
  private declaration(): void {
    const end = this.text.indexOf("?>", this.pos);
    if (end < 0) {
      throw new XmlParseError("unexpected-end", this.pos, "unterminated XML declaration");
    }
    const body = this.text.slice(this.pos + 5, end);
    const pattern = /^\s+version\s*=\s*(?:"1\.0"|'1\.0')(?:\s+encoding\s*=\s*(?:"([A-Za-z][A-Za-z0-9._-]*)"|'([A-Za-z][A-Za-z0-9._-]*)'))?(?:\s+standalone\s*=\s*(?:"(?:yes|no)"|'(?:yes|no)'))?\s*$/;
    const match = pattern.exec(body);
    if (match === null) {
      throw new XmlParseError("malformed", this.pos, "malformed XML declaration (only version 1.0 is read)");
    }
    const encoding = match[1] ?? match[2];
    if (encoding !== undefined && encoding.toLowerCase() !== "utf-8") {
      throw new XmlParseError("encoding", this.pos, "the XML declaration names an encoding other than UTF-8");
    }
    this.pos = end + 2;
  }

  /** White space and comments; a processing instruction or a document type declaration is refused. */
  private misc(): void {
    for (;;) {
      this.skipSpace();
      if (this.text.startsWith("<!--", this.pos)) {
        this.comment();
      } else if (this.text.startsWith("<?", this.pos)) {
        throw new XmlParseError("processing-instruction", this.pos, "processing instructions are not read");
      } else if (this.text.startsWith("<!DOCTYPE", this.pos)) {
        throw new XmlParseError("doctype", this.pos, "a document type declaration is not read");
      } else {
        return;
      }
    }
  }

  private skipSpace(): void {
    while (this.pos < this.text.length && isXmlSpace(this.text.charCodeAt(this.pos))) {
      this.pos++;
    }
  }

  private comment(): void {
    const start = this.pos + 4;
    const end = this.text.indexOf("--", start);
    if (end < 0) {
      throw new XmlParseError("unexpected-end", this.pos, "unterminated comment");
    }
    if (this.text.charCodeAt(end + 2) !== GT) {
      throw new XmlParseError("malformed", end, "'--' inside a comment");
    }
    this.checkChars(start, end);
    this.pos = end + 3;
  }

  private count(): void {
    if (++this.tokens > this.limits.maxTokens) {
      throw new XmlParseError("token-limit", this.pos, `the document has more than ${this.limits.maxTokens} elements, attributes and texts`);
    }
    if ((this.tokens & 1023) === 0) {
      this.checkpoint?.();
    }
  }

  private name(): string {
    const start = this.pos;
    while (this.pos < this.text.length) {
      const code = this.text.charCodeAt(this.pos);
      if (isXmlSpace(code) || code === GT || code === SLASH || code === EQ || code === LT || code === QUOT || code === APOS) {
        break;
      }
      this.pos++;
    }
    const name = this.text.slice(start, this.pos);
    const colon = name.indexOf(":");
    if (colon < 0 ? !isNcName(name) : !isNcName(name.slice(0, colon)) || !isNcName(name.slice(colon + 1))) {
      throw new XmlParseError("malformed", start, "invalid element or attribute name");
    }
    return name;
  }

  private element(scope: ReadonlyMap<string, string>, defaultNs: string, depth: number): XmlElement {
    if (depth > this.limits.maxDepth) {
      throw new XmlParseError("depth-limit", this.pos, `elements nest deeper than ${this.limits.maxDepth} levels`);
    }
    this.count();
    const tagStart = this.pos;
    this.pos++; // <
    const qname = this.name();
    const raw: { readonly name: string; readonly value: string; readonly offset: number }[] = [];
    let empty = false;
    for (;;) {
      const before = this.pos;
      this.skipSpace();
      const code = this.text.charCodeAt(this.pos);
      if (this.pos >= this.text.length) {
        throw new XmlParseError("unexpected-end", this.pos, "unterminated start tag");
      }
      if (code === GT) {
        this.pos++;
        break;
      }
      if (code === SLASH && this.text.charCodeAt(this.pos + 1) === GT) {
        this.pos += 2;
        empty = true;
        break;
      }
      if (this.pos === before) {
        throw new XmlParseError("malformed", this.pos, "attributes are separated by white space");
      }
      const offset = this.pos;
      const name = this.name();
      this.skipSpace();
      if (this.text.charCodeAt(this.pos) !== EQ) {
        throw new XmlParseError("malformed", this.pos, "an attribute has a value");
      }
      this.pos++;
      this.skipSpace();
      raw.push({ name, value: this.attributeValue(), offset });
    }

    // namespace declarations first, then the element's and the attributes' names
    let ownScope: Map<string, string> | undefined;
    let ownDefault = defaultNs;
    const seenNames = new Set<string>();
    for (const attribute of raw) {
      if (seenNames.has(attribute.name)) {
        throw new XmlParseError("duplicate-attribute", attribute.offset, "an attribute appears twice");
      }
      seenNames.add(attribute.name);
      if (attribute.name === "xmlns") {
        if (attribute.value === xmlNamespace || attribute.value === xmlnsNamespace) {
          throw new XmlParseError("namespace", attribute.offset, "a reserved namespace cannot be the default namespace");
        }
        ownDefault = attribute.value;
      } else if (attribute.name.startsWith("xmlns:")) {
        const prefix = attribute.name.slice(6);
        if (prefix === "xmlns" || attribute.value === "" || attribute.value === xmlnsNamespace || (prefix === "xml") !== (attribute.value === xmlNamespace)) {
          throw new XmlParseError("namespace", attribute.offset, "invalid namespace declaration");
        }
        ownScope ??= new Map(scope);
        ownScope.set(prefix, attribute.value);
      }
    }
    const inScope = ownScope ?? scope;
    const resolve = (prefix: string, offset: number): string => {
      const ns = inScope.get(prefix);
      if (ns === undefined) {
        throw new XmlParseError("namespace", offset, "a prefix has no namespace declaration");
      }
      return ns;
    };
    const colon = qname.indexOf(":");
    const local = colon < 0 ? qname : qname.slice(colon + 1);
    const ns = colon < 0 ? ownDefault : resolve(qname.slice(0, colon), tagStart);
    const attributes: XmlAttribute[] = [];
    const expanded = new Set<string>();
    for (const attribute of raw) {
      if (attribute.name === "xmlns" || attribute.name.startsWith("xmlns:")) {
        continue;
      }
      this.count();
      const split = attribute.name.indexOf(":");
      const attributeLocal = split < 0 ? attribute.name : attribute.name.slice(split + 1);
      const attributeNs = split < 0 ? "" : resolve(attribute.name.slice(0, split), attribute.offset);
      const key = attributeNs + "\u0000" + attributeLocal;
      if (expanded.has(key)) {
        throw new XmlParseError("duplicate-attribute", attribute.offset, "an attribute appears twice");
      }
      expanded.add(key);
      attributes.push({ local: attributeLocal, ns: attributeNs, value: attribute.value });
    }

    const children: XmlChild[] = [];
    if (!empty) {
      let text = "";
      let hasText = false;
      const flush = (): void => {
        if (hasText) {
          this.count();
          children.push({ kind: "text", value: text });
          text = "";
          hasText = false;
        }
      };
      for (;;) {
        if (this.pos >= this.text.length) {
          throw new XmlParseError("unexpected-end", this.pos, "unterminated element");
        }
        const code = this.text.charCodeAt(this.pos);
        if (code === LT) {
          const next = this.text.charCodeAt(this.pos + 1);
          if (next === SLASH) {
            this.pos += 2;
            const closing = this.name();
            this.skipSpace();
            if (closing !== qname || this.text.charCodeAt(this.pos) !== GT) {
              throw new XmlParseError("malformed", this.pos, "the end tag does not match the start tag");
            }
            this.pos++;
            break;
          }
          if (this.text.startsWith("<!--", this.pos)) {
            this.comment();
            continue;
          }
          if (this.text.startsWith("<![CDATA[", this.pos)) {
            const start = this.pos + 9;
            const end = this.text.indexOf("]]>", start);
            if (end < 0) {
              throw new XmlParseError("unexpected-end", this.pos, "unterminated CDATA section");
            }
            this.checkChars(start, end);
            text += this.text.slice(start, end);
            hasText = true;
            this.pos = end + 3;
            continue;
          }
          if (next === QUESTION) {
            throw new XmlParseError("processing-instruction", this.pos, "processing instructions are not read");
          }
          if (next === BANG) {
            throw new XmlParseError("malformed", this.pos, "unexpected markup declaration in content");
          }
          flush();
          children.push(this.element(inScope, ownDefault, depth + 1));
          continue;
        }
        if (code === AMP) {
          text += this.reference();
          hasText = true;
          continue;
        }
        const start = this.pos;
        while (this.pos < this.text.length) {
          const c = this.text.charCodeAt(this.pos);
          if (c === LT || c === AMP) {
            break;
          }
          if (c === GT && this.text.charCodeAt(this.pos - 1) === 0x5d && this.text.charCodeAt(this.pos - 2) === 0x5d) {
            throw new XmlParseError("malformed", this.pos, "']]>' in character content");
          }
          this.pos++;
        }
        this.checkChars(start, this.pos);
        text += this.text.slice(start, this.pos);
        hasText = true;
      }
      flush();
    }
    return { kind: "element", local, ns, attributes, children };
  }

  /** A quoted attribute value with references; white space characters are spaces (attribute-value normalization, XML 1.0 §3.3.3). */
  private attributeValue(): string {
    const quote = this.text.charCodeAt(this.pos);
    if (quote !== QUOT && quote !== APOS) {
      throw new XmlParseError("malformed", this.pos, "an attribute value is quoted");
    }
    this.pos++;
    let value = "";
    for (;;) {
      if (this.pos >= this.text.length) {
        throw new XmlParseError("unexpected-end", this.pos, "unterminated attribute value");
      }
      const code = this.text.charCodeAt(this.pos);
      if (code === quote) {
        this.pos++;
        return value;
      }
      if (code === LT) {
        throw new XmlParseError("malformed", this.pos, "'<' in an attribute value");
      }
      if (code === AMP) {
        value += this.reference();
        continue;
      }
      const start = this.pos;
      while (this.pos < this.text.length) {
        const c = this.text.charCodeAt(this.pos);
        if (c === quote || c === LT || c === AMP) {
          break;
        }
        this.pos++;
      }
      this.checkChars(start, this.pos);
      value += this.text.slice(start, this.pos).replace(/[\t\n]/g, " ");
    }
  }

  /** `&name;`, `&#N;` or `&#xH;`: the five predefined entities and character references to any Unicode scalar value. */
  private reference(): string {
    const start = this.pos;
    const end = this.text.indexOf(";", start);
    if (end < 0 || end - start > 16) {
      throw new XmlParseError("entity", start, "unterminated reference");
    }
    const body = this.text.slice(start + 1, end);
    this.pos = end + 1;
    if (body.startsWith("#")) {
      const hex = body.startsWith("#x");
      const digits = body.slice(hex ? 2 : 1);
      if (!(hex ? /^[0-9A-Fa-f]+$/ : /^[0-9]+$/).test(digits)) {
        throw new XmlParseError("entity", start, "malformed character reference");
      }
      const code = parseInt(digits, hex ? 16 : 10);
      // the server's reader takes any scalar value here (CheckCharacters is off); a surrogate is no character
      if (code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff)) {
        throw new XmlParseError("entity", start, "a character reference to no Unicode scalar value");
      }
      return String.fromCodePoint(code);
    }
    const replacement = predefined[body];
    if (replacement === undefined) {
      throw new XmlParseError("entity", start, "only the predefined entities are read");
    }
    return replacement;
  }

  private checkChars(start: number, end: number): void {
    for (let i = start; i < end; i++) {
      const code = this.text.charCodeAt(i);
      if (code < 0x20 ? code !== 0x09 && code !== 0x0a : code === 0xfffe || code === 0xffff) {
        throw new XmlParseError("character", i, "a character XML does not allow");
      }
      if (code >= 0xd800 && code <= 0xdbff) {
        const low = this.text.charCodeAt(i + 1);
        if (!(low >= 0xdc00 && low <= 0xdfff)) {
          throw new XmlParseError("character", i, "a lone surrogate");
        }
        i++;
      } else if (code >= 0xdc00 && code <= 0xdfff) {
        throw new XmlParseError("character", i, "a lone surrogate");
      }
    }
  }
}
