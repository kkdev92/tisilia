/** Media type parsing with ASCII case-insensitive type/subtype. No `startsWith("application/json")` shortcuts. */
export interface MediaType {
  readonly type: string;
  readonly subtype: string;
  readonly essence: string;
  readonly charset: string | undefined;
  readonly parameters: ReadonlyMap<string, string>;
}

/** RFC 9110 §5.6.2 tchar. */
function isTokenChar(code: number): boolean {
  return (
    (code >= 0x30 && code <= 0x39) ||
    (code >= 0x41 && code <= 0x5a) ||
    (code >= 0x61 && code <= 0x7a) ||
    "!#$%&'*+-.^_`|~".includes(String.fromCharCode(code))
  );
}

/** RFC 9110 §5.6.4 qdtext: HTAB, SP, %x21, %x23-5B, %x5D-7E and obs-text (%x80-FF). */
function isQdtext(code: number): boolean {
  return code === 0x09 || code === 0x20 || code === 0x21 || (code >= 0x23 && code <= 0x5b) || (code >= 0x5d && code <= 0x7e) || (code >= 0x80 && code <= 0xff);
}

/**
 * RFC 9110 §8.3.1 `media-type = type "/" subtype parameters` with §5.6.6 `parameters = *( OWS ";" OWS [ parameter ] )`,
 * `parameter = token "=" ( token / quoted-string )` — no whitespace around "=", a quoted-string may hold ";" and quoted-pairs.
 * Undefined for anything else; the first occurrence of a parameter name wins.
 */
export function parseMediaType(value: string): MediaType | undefined {
  // Trimmed by index: a trailing-anchored `[ \t]+$` is retried from every blank of a run that does not end the value, which
  // takes time quadratic in the length of a header the server chooses.
  let start = 0;
  let end = value.length;
  while (start < end && (value[start] === " " || value[start] === "\t")) {
    start++;
  }
  while (end > start && (value[end - 1] === " " || value[end - 1] === "\t")) {
    end--;
  }
  const text = value.slice(start, end);
  let i = 0;
  const token = (): string => {
    const begin = i;
    while (i < text.length && isTokenChar(text.charCodeAt(i))) {
      i++;
    }
    return text.slice(begin, i);
  };
  const ows = (): void => {
    while (i < text.length && (text[i] === " " || text[i] === "\t")) {
      i++;
    }
  };
  const type = token();
  if (type.length === 0 || text[i] !== "/") {
    return undefined;
  }
  i++;
  const subtype = token();
  if (subtype.length === 0) {
    return undefined;
  }
  const parameters = new Map<string, string>();
  for (;;) {
    ows();
    if (i === text.length) {
      break;
    }
    if (text[i] !== ";") {
      return undefined;
    }
    i++;
    ows();
    if (i === text.length || text[i] === ";") {
      continue; // an empty parameter
    }
    const name = token();
    if (name.length === 0 || text[i] !== "=") {
      return undefined;
    }
    i++;
    let parameterValue = "";
    if (text[i] === '"') {
      i++;
      for (;;) {
        if (i >= text.length) {
          return undefined; // unterminated quoted-string
        }
        const code = text.charCodeAt(i);
        if (code === 0x22) {
          i++;
          break;
        }
        if (code === 0x5c) {
          const quoted = i + 1 < text.length ? text.charCodeAt(i + 1) : -1;
          if (!(quoted === 0x09 || (quoted >= 0x20 && quoted <= 0x7e) || (quoted >= 0x80 && quoted <= 0xff))) {
            return undefined;
          }
          parameterValue += text[i + 1];
          i += 2;
          continue;
        }
        if (!isQdtext(code)) {
          return undefined;
        }
        parameterValue += text[i];
        i++;
      }
    } else {
      parameterValue = token();
      if (parameterValue.length === 0) {
        return undefined;
      }
    }
    const lower = name.toLowerCase();
    if (!parameters.has(lower)) {
      parameters.set(lower, parameterValue);
    }
  }
  const lowerType = type.toLowerCase();
  const lowerSubtype = subtype.toLowerCase();
  return {
    type: lowerType,
    subtype: lowerSubtype,
    essence: lowerType + "/" + lowerSubtype,
    charset: parameters.get("charset")?.toLowerCase(),
    parameters,
  };
}

/** JSON media types the runtime accepts: an explicit list, never a `+json` suffix rule. */
export const jsonMediaEssences: ReadonlySet<string> = new Set(["application/json", "text/json", "application/problem+json"]);

export function isUtf8OrUnspecified(media: MediaType): boolean {
  return media.charset === undefined || media.charset === "utf-8";
}

export function essenceOf(value: string): string {
  const m = parseMediaType(value);
  return m === undefined ? value.trim().toLowerCase() : m.essence;
}
