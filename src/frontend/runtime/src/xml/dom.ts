/**
 * The XML document model of XML bodies (MVC's XmlSerializer formatters): elements with expanded names — a local name and a namespace
 * URI, "" for none — attributes and children. Namespace declarations are not attributes; comments are not content; adjacent text and
 * CDATA are one text node.
 */
export interface XmlElement {
  readonly kind: "element";
  readonly local: string;
  readonly ns: string;
  readonly attributes: readonly XmlAttribute[];
  readonly children: readonly XmlChild[];
}

export interface XmlAttribute {
  readonly local: string;
  readonly ns: string;
  readonly value: string;
}

export interface XmlText {
  readonly kind: "text";
  readonly value: string;
}

export type XmlChild = XmlElement | XmlText;

export const xsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";
export const xmlNamespace = "http://www.w3.org/XML/1998/namespace";
export const xmlnsNamespace = "http://www.w3.org/2000/xmlns/";

/** XML white space (S): space, tab, line feed and carriage return. */
export function isXmlSpace(code: number): boolean {
  return code === 0x20 || code === 0x09 || code === 0x0a || code === 0x0d;
}

function isNameStart(code: number): boolean {
  return (code >= 0x61 && code <= 0x7a) || (code >= 0x41 && code <= 0x5a) || code === 0x5f
    || (code >= 0xc0 && code <= 0xd6) || (code >= 0xd8 && code <= 0xf6) || (code >= 0xf8 && code <= 0x2ff) || (code >= 0x370 && code <= 0x37d)
    || (code >= 0x37f && code <= 0x1fff) || (code >= 0x200c && code <= 0x200d) || (code >= 0x2070 && code <= 0x218f) || (code >= 0x2c00 && code <= 0x2fef)
    || (code >= 0x3001 && code <= 0xd7ff) || (code >= 0xf900 && code <= 0xfdcf) || (code >= 0xfdf0 && code <= 0xfffd) || (code >= 0x10000 && code <= 0xeffff);
}

function isNameChar(code: number): boolean {
  return isNameStart(code) || code === 0x2d || code === 0x2e || (code >= 0x30 && code <= 0x39) || code === 0xb7 || (code >= 0x300 && code <= 0x36f) || (code >= 0x203f && code <= 0x2040);
}

/** An XML name without a colon (XML Namespaces NCName, XML 1.0 fifth edition name characters). */
export function isNcName(name: string): boolean {
  if (name.length === 0) {
    return false;
  }
  let first = true;
  for (const ch of name) {
    const code = ch.codePointAt(0)!;
    if (first ? !isNameStart(code) : !isNameChar(code)) {
      return false;
    }
    first = false;
  }
  return true;
}

/** A character XML 1.0 allows in a document as itself (Char): tab, line feed, carriage return and the non-control ranges. */
export function isXmlChar(code: number): boolean {
  return code === 0x09 || code === 0x0a || code === 0x0d || (code >= 0x20 && code <= 0xd7ff) || (code >= 0xe000 && code <= 0xfffd) || (code >= 0x10000 && code <= 0x10ffff);
}
