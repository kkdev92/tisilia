/** The default limits. Every limit is a positive safe integer and applies to both decoder and HTTP stream. */
export interface Limits {
  readonly maxBodyBytes: number;
  readonly maxDepth: number;
  readonly maxTokens: number;
  readonly maxNumberCharacters: number;
  readonly timeoutMs: number;
  readonly maxDiagnosticBytes: number;
}

export const defaultLimits: Limits = Object.freeze({
  maxBodyBytes: 16_777_216,
  maxDepth: 64,
  maxTokens: 1_000_000,
  maxNumberCharacters: 4096,
  timeoutMs: 30_000,
  maxDiagnosticBytes: 262_144,
});

/**
 * The defaults overridden by the given limits (an undefined entry keeps its default), validated: an invalid limit is a
 * configuration error, never a silently disabled check (`maxBodyBytes: NaN` would otherwise compare false forever).
 */
export function resolveLimits(limits?: Partial<Limits>): Limits {
  if (limits === undefined || limits === defaultLimits) {
    return defaultLimits;
  }
  const resolved: { -readonly [K in keyof Limits]: number } = { ...defaultLimits };
  for (const name of Object.keys(defaultLimits) as (keyof Limits)[]) {
    const value = limits[name];
    if (value !== undefined) {
      resolved[name] = value;
    }
  }
  validateLimits(resolved);
  return resolved;
}

export function validateLimits(limits: Limits): void {
  const check = (name: keyof Limits, min: number, max: number): void => {
    const v = limits[name];
    if (!Number.isSafeInteger(v) || v < min || v > max) {
      throw new RangeError(`limits.${name} must be an integer in [${min}, ${max}]`);
    }
  };
  check("maxBodyBytes", 1, Number.MAX_SAFE_INTEGER);
  check("maxDepth", 1, 1024);
  check("maxTokens", 1, Number.MAX_SAFE_INTEGER);
  check("maxNumberCharacters", 1, 4096);
  check("timeoutMs", 1, 2_147_483_647);
  check("maxDiagnosticBytes", 0, Number.MAX_SAFE_INTEGER);
}
