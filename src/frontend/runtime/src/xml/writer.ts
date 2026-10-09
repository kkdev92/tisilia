import { CodecError } from "../codec/errors.js";
import { isNcName, isXmlChar, xmlNamespace, xsiNamespace, type XmlElement } from "./dom.js";

/**
 * Writes an XML document without a declaration. An element's namespace is a default-namespace declaration; an attribute in a namespace
 * gets a prefix declared on its element. Characters XML would change or refuse on reading — a carriage return, a tab or line feed in
 * an attribute, control characters, U+FFFE and U+FFFF — are written as character references, which MVC's XmlSerializer formatters read
 * as the characters themselves.
 */
export function writeXml(root: XmlElement): string {
  const out: string[] = [];
  write(root, "", new Map(), out);
  return out.join("");
}

function write(element: XmlElement, inheritedNs: string, inherited: ReadonlyMap<string, string>, out: string[]): void {
  if (!isNcName(element.local)) {
    throw new CodecError("unsupported", "", "an XML element name is not an NCName");
  }
  let declarations = "";
  if (element.ns !== inheritedNs) {
    declarations += ` xmlns="${escapeAttribute(element.ns)}"`;
  }
  let prefixes = inherited;
  const attributes: string[] = [];
  for (const attribute of element.attributes) {
    if (!isNcName(attribute.local)) {
      throw new CodecError("unsupported", "", "an XML attribute name is not an NCName");
    }
    let qualified = attribute.local;
    if (attribute.ns === xmlNamespace) {
      qualified = "xml:" + attribute.local;
    } else if (attribute.ns !== "") {
      let prefix = prefixes.get(attribute.ns);
      if (prefix === undefined) {
        const used = new Set(prefixes.values());
        prefix = attribute.ns === xsiNamespace && !used.has("xsi") ? "xsi" : nextPrefix(used);
        const own = new Map(prefixes);
        own.set(attribute.ns, prefix);
        prefixes = own;
        declarations += ` xmlns:${prefix}="${escapeAttribute(attribute.ns)}"`;
      }
      qualified = prefix + ":" + attribute.local;
    }
    attributes.push(` ${qualified}="${escapeAttribute(attribute.value)}"`);
  }
  out.push("<", element.local, declarations, ...attributes);
  if (element.children.length === 0) {
    out.push("/>");
    return;
  }
  out.push(">");
  for (const child of element.children) {
    if (child.kind === "text") {
      out.push(escapeText(child.value));
    } else {
      write(child, element.ns, prefixes, out);
    }
  }
  out.push("</", element.local, ">");
}

function nextPrefix(used: ReadonlySet<string>): string {
  for (let i = 1; ; i++) {
    const candidate = "p" + i;
    if (!used.has(candidate)) {
      return candidate;
    }
  }
}

function reference(code: number): string {
  return "&#x" + code.toString(16).toUpperCase() + ";";
}

function escape(text: string, attribute: boolean): string {
  let out = "";
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    let replacement: string | undefined;
    if (code === 0x26) {
      replacement = "&amp;";
    } else if (code === 0x3c) {
      replacement = "&lt;";
    } else if (code === 0x3e) {
      replacement = "&gt;";
    } else if (code === 0x22 && attribute) {
      replacement = "&quot;";
    } else if (code === 0x0d || (attribute && (code === 0x09 || code === 0x0a))) {
      replacement = reference(code);
    } else if (code >= 0xd800 && code <= 0xdbff) {
      const low = text.charCodeAt(i + 1);
      if (!(low >= 0xdc00 && low <= 0xdfff)) {
        throw new CodecError("domain-rule", "", "XML text contains a lone surrogate");
      }
      i++;
      continue;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      throw new CodecError("domain-rule", "", "XML text contains a lone surrogate");
    } else if (!isXmlChar(code)) {
      replacement = reference(code);
    }
    if (replacement !== undefined) {
      out += text.slice(start, i) + replacement;
      start = i + 1;
    }
  }
  return start === 0 ? text : out + text.slice(start);
}

export function escapeText(text: string): string {
  return escape(text, false);
}

export function escapeAttribute(text: string): string {
  return escape(text, true);
}
