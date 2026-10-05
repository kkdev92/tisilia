/** Bounded, linear Content-Disposition parser. Header visibility is enforced by the caller before invoking it. */
export function suggestedFileName(header: string | undefined): string | undefined {
  if (header === undefined || header.length > 8192 || /[\u0000-\u001f\u007f]/.test(header)) { return undefined; }
  const token = /^[!#$%&'*+\-.^_`|~0-9A-Za-z]$/;
  const params = new Map<string, string>();
  let i = 0;
  const whitespace = (): void => { while (header[i] === " ") { i++; } };
  const readToken = (): string => { const start = i; while (i < header.length && token.test(header[i]!)) { i++; } return header.slice(start, i); };
  whitespace();
  if (readToken() === "") { return undefined; }
  whitespace();
  while (i < header.length) {
    if (header[i++] !== ";") { return undefined; }
    whitespace();
    const name = readToken().toLowerCase();
    whitespace();
    if (name === "" || header[i++] !== "=") { return undefined; }
    whitespace();
    let value = "";
    if (header[i] === '"') {
      i++;
      let closed = false;
      while (i < header.length) {
        const c = header[i++]!;
        if (c === '"') { closed = true; break; }
        if (c === "\\") {
          if (i === header.length) { return undefined; }
          value += header[i++]!;
        } else { value += c; }
      }
      if (!closed) { return undefined; }
    } else { value = readToken(); if (value === "") { return undefined; } }
    if (params.has(name)) { return undefined; }
    params.set(name, value);
    whitespace();
  }
  const extended = params.get("filename*");
  if (extended !== undefined) {
    const match = /^UTF-8'([A-Za-z0-9-]*)'((?:[!#$&+\-.^_`|~0-9A-Za-z]|%[0-9a-fA-F]{2})*)$/i.exec(extended);
    if (match !== null) {
      try { const safe = safeFileName(decodeURIComponent(match[2]!)); if (safe !== undefined) { return safe; } } catch { /* a unique ordinary filename may still be used */ }
    }
  }
  const ordinary = params.get("filename");
  return ordinary !== undefined && /^[\x20-\x7e]+$/.test(ordinary) ? safeFileName(ordinary) : undefined;
}

/** Advisory basename, not a filesystem path or a guarantee that opening its content is safe. */
export function safeFileName(name: string): string | undefined {
  const last = name.split(/[\\/]/).at(-1)?.trim() ?? "";
  // Trailing dots and spaces are stripped by index: `/[. ]+$/` is retried from every dot and space of a run that does not
  // end the name, which takes time quadratic in its length.
  let end = last.length;
  while (end > 0 && (last[end - 1] === "." || last[end - 1] === " ")) { end--; }
  const base = last.slice(0, end);
  if (!base || base.length > 255 || /^[.]+$/.test(base) || /[\u0000-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069<>:"|?*]/.test(base)
    || /^(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)/i.test(base)) { return undefined; }
  return base;
}
