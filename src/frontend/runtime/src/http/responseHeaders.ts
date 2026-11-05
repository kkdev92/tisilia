const credentialHeaders = new Set(["authorization", "proxy-authorization", "cookie", "cookie2", "set-cookie", "set-cookie2"]);

/** Response results and hydration payloads carry only declared, non-credential headers, including for hand-written descriptors. */
export function exposedResponseHeaders(headers: readonly (readonly [string, string])[], exposed: readonly string[]): (readonly [string, string])[] {
  const allowed = new Set(exposed.map(name => name.toLowerCase()));
  return headers.flatMap(([name, value]) => {
    const lower = name.toLowerCase();
    return allowed.has(lower) && !credentialHeaders.has(lower) ? [[lower, value] as const] : [];
  });
}
