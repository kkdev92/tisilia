import { CodecError } from "../codec/errors.js";

/**
 * URL and header construction rules. Values are never string-concatenated into a path
 * without percent-encoding; baseUrl is an explicitly allowed origin; user input can never become an absolute URL.
 */

const unreserved = /[A-Za-z0-9\-._~]/;

function percentEncode(value: string, keep: (c: string) => boolean): string {
  if ([...value].some(c => c.length === 1 && c.charCodeAt(0) >= 0xd800 && c.charCodeAt(0) <= 0xdfff)) {
    throw new CodecError("grammar", "", "unpaired Unicode surrogate is not a valid route or query value");
  }
  const bytes = new TextEncoder().encode(value);
  let out = "";
  for (const b of bytes) {
    const c = String.fromCharCode(b);
    if (b < 0x80 && keep(c)) {
      out += c;
    } else {
      out += "%" + b.toString(16).toUpperCase().padStart(2, "0");
    }
  }
  return out;
}

/** One path segment: everything but unreserved characters is percent-encoded; '/' is never left raw. */
export function encodePathSegment(value: string, path = ""): string {
  if (value.length === 0) {
    throw new CodecError("grammar", path, "path segment cannot be empty");
  }
  if (value.includes("/") || value.includes("\\")) {
    throw new CodecError("grammar", path, "path segment cannot contain a slash (per-route qualification required for %2F)");
  }
  if (value === "." || value === "..") {
    throw new CodecError("grammar", path, "dot segments are not allowed");
  }
  return percentEncode(value, (c) => unreserved.test(c));
}

/** Query name/value component: space is %20, plus is %2B, only unreserved characters stay raw. */
export function encodeQueryComponent(value: string): string {
  return percentEncode(value, (c) => unreserved.test(c));
}

const forbiddenHeaders = new Set([
  "accept-charset",
  "accept-encoding",
  "access-control-request-headers",
  "access-control-request-method",
  "connection",
  "content-length",
  "cookie",
  "cookie2",
  "date",
  "dnt",
  "expect",
  "host",
  "keep-alive",
  "origin",
  "referer",
  "set-cookie",
  "te",
  "trailer",
  "transfer-encoding",
  "upgrade",
  "via",
]);

/** WHATWG Fetch forbidden request-header names plus credential headers reserved for the provider. */
export function isForbiddenRequestHeader(name: string): boolean {
  const lower = name.toLowerCase();
  return forbiddenHeaders.has(lower) || lower.startsWith("proxy-") || lower.startsWith("sec-") || lower === "x-http-method" || lower === "x-http-method-override" || lower === "x-method-override";
}

export function isCredentialHeader(name: string): boolean {
  const lower = name.toLowerCase();
  return lower === "authorization" || lower === "proxy-authorization" || lower === "cookie";
}

const headerToken = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$/;

/** A header name is an RFC 9110 token; unlike `validateHeaderName` this does not apply the browser's forbidden list (credentials). */
export function validateHeaderToken(name: string, path = ""): string {
  if (!headerToken.test(name)) {
    throw new CodecError("grammar", path, "invalid header name");
  }
  return name;
}

export function validateHeaderName(name: string, path = ""): string {
  if (!headerToken.test(name)) {
    throw new CodecError("grammar", path, "invalid header name");
  }
  if (isForbiddenRequestHeader(name)) {
    throw new CodecError("unsupported", path, "header cannot be set by a browser fetch");
  }
  return name;
}

/** Header values: no CR/LF/NUL and no non-ASCII; leading/trailing whitespace is rejected instead of silently trimmed. */
export function validateHeaderValue(value: string, path = ""): string {
  for (let i = 0; i < value.length; i++) {
    const c = value.charCodeAt(i);
    if (c === 0x0d || c === 0x0a || c === 0x00 || c > 0x7e || (c < 0x20 && c !== 0x09)) {
      throw new CodecError("grammar", path, "header value contains a forbidden character");
    }
  }
  if (value !== value.trim()) {
    throw new CodecError("grammar", path, "header value must not have leading or trailing whitespace");
  }
  return value;
}

export interface QueryEntry {
  readonly name: string;
  readonly value: string;
}

export interface BuiltUrl {
  readonly url: URL;
  /** Percent-encoded path without origin, query or fragment (request identity input). */
  readonly encodedPath: string;
  /** Percent-encoded query entries in send order (request identity input). */
  readonly queryEntries: readonly QueryEntry[];
}

/** Validates a base URL: absolute http(s), no credentials, no query, no fragment. */
export function validateBaseUrl(baseUrl: string): URL {
  let url: URL;
  try {
    url = new URL(baseUrl);
  } catch {
    throw new CodecError("grammar", "", "baseUrl is not an absolute URL");
  }
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new CodecError("unsupported", "", "baseUrl must use http or https");
  }
  if (url.username !== "" || url.password !== "") {
    throw new CodecError("unsupported", "", "baseUrl must not contain credentials");
  }
  if (url.search !== "" || url.hash !== "") {
    throw new CodecError("unsupported", "", "baseUrl must not contain a query or fragment");
  }
  return url;
}

/** A route template as literal text and parameters (their names: what precedes constraints, a default or the optional mark). */
type RoutePart = { readonly literal: string } | { readonly name: string };

/** As RouteParameterParser.ParseRouteParameter: a leading `**` or `*`, a trailing `?`, then the name up to the first ':' or '='. */
function parameterName(body: string): string {
  let text = body.startsWith("**") ? body.slice(2) : body.startsWith("*") ? body.slice(1) : body;
  if (text.endsWith("?")) {
    text = text.slice(0, -1);
  }
  for (let i = 1; i < text.length; i++) {
    if (text[i] === ":" || text[i] === "=") {
      return text.slice(0, i);
    }
  }
  return text;
}

/**
 * Splits an ASP.NET Core route template the way its RoutePatternParser does: `{{` and `}}` are literal braces, in literal text
 * and inside a parameter (`{code:regex(^\d{{3}}$)}`, the routing documentation's own example); a single `{` opens a parameter
 * and the next single `}` closes it. A constraint may hold `?`, `#` or `/` (a regex); literal text may not hold `?`.
 */
function routeParts(route: string): RoutePart[] {
  const parts: RoutePart[] = [];
  let literal = "";
  let i = 0;
  while (i < route.length) {
    const c = route[i]!;
    if ((c === "{" || c === "}") && route[i + 1] === c) {
      literal += c;
      i += 2;
    } else if (c === "}") {
      throw new CodecError("grammar", "", "route template has an unbalanced '}'");
    } else if (c === "{") {
      let body = "";
      let j = i + 1;
      for (;;) {
        const d = route[j];
        if (d === undefined) {
          throw new CodecError("grammar", "", "route template has an unterminated parameter");
        }
        if (d === "{" || (d === "}" && route[j + 1] === "}")) {
          if (route[j + 1] !== d) {
            throw new CodecError("grammar", "", "route template has an unescaped '{' inside a parameter");
          }
          body += d;
          j += 2;
        } else if (d === "}") {
          break;
        } else {
          body += d;
          j += 1;
        }
      }
      if (literal.length > 0) {
        parts.push({ literal });
        literal = "";
      }
      parts.push({ name: parameterName(body) });
      i = j + 1;
    } else {
      literal += c;
      i += 1;
    }
  }
  if (literal.length > 0) {
    parts.push({ literal });
  }
  return parts;
}

/**
 * Substitutes encoded path parameters into a route template (`/users/{id:guid}`), appends encoded query entries and
 * resolves against the base URL. The result must keep the base origin.
 */
export function buildUrl(baseUrl: string, route: string, pathValues: ReadonlyMap<string, string>, queryEntries: readonly QueryEntry[]): BuiltUrl {
  const base = validateBaseUrl(baseUrl);
  if (!route.startsWith("/")) {
    throw new CodecError("grammar", "", "route must be an absolute path template without query or fragment");
  }
  let encodedPath = "";
  for (const part of routeParts(route)) {
    if ("literal" in part) {
      if (part.literal.includes("?") || part.literal.includes("#")) {
        throw new CodecError("grammar", "", "route must be an absolute path template without query or fragment");
      }
      // ASP.NET Core matches literal text against the decoded path; URL would encode the braces anyway
      encodedPath += part.literal.replaceAll("{", "%7B").replaceAll("}", "%7D");
    } else {
      const value = pathValues.get(part.name);
      if (value === undefined) {
        throw new CodecError("missing-required", "/" + part.name, `path parameter '${part.name}' has no value`);
      }
      encodedPath += value;
    }
  }
  const basePath = base.pathname.endsWith("/") ? base.pathname.slice(0, -1) : base.pathname;
  let full = basePath + encodedPath;
  if (queryEntries.length > 0) {
    full += "?" + queryEntries.map((e) => (e.value.length === 0 && e.name.length > 0 ? e.name : e.name + "=" + e.value)).join("&");
  }
  const url = new URL(full, base);
  if (url.origin !== base.origin || (basePath !== "" && url.pathname !== basePath && !url.pathname.startsWith(basePath + "/"))) {
    throw new CodecError("unsupported", "", "resolved URL left the allowed origin");
  }
  return { url, encodedPath: url.pathname, queryEntries };
}
