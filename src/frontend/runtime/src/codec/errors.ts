/**
 * Codec failure: a stable safe code, the RFC 6901 pointer of the offending value and an
 * optional detail. Raw values are never copied into the message; diagnostics are redacted upstream.
 */
export type CodecErrorCode =
  | "type-mismatch"
  | "range"
  | "grammar"
  | "precision"
  | "missing-required"
  | "unexpected-property"
  | "duplicate-property"
  | "null-not-allowed"
  | "undefined-not-allowed"
  | "domain-rule"
  | "unknown-discriminator"
  | "key-collision"
  | "unsupported"
  | "limit"
  | "cancelled";

export class CodecError extends Error {
  constructor(
    readonly code: CodecErrorCode,
    readonly path: string,
    message: string,
    readonly codecId?: string,
  ) {
    super(message);
    this.name = "CodecError";
  }

  /** Re-anchors the error under a parent path segment (used by structural codecs). */
  static nested(error: unknown, parentPath: string): CodecError {
    const codecError = asCodecError(error);
    if (codecError !== undefined) {
      return new CodecError(codecError.code, parentPath + codecError.path, codecError.message, codecError.codecId);
    }
    throw error;
  }
}

/**
 * The CodecError a thrown value stands for. Codec modules are self-contained — they import nothing from the runtime —
 * and signal a failure with an Error whose name is "CodecError" and whose code (and path) are strings; such an error is
 * classified exactly like the runtime's own. Anything else is not a codec failure.
 */
export function asCodecError(error: unknown): CodecError | undefined {
  if (error instanceof CodecError) {
    return error;
  }
  if (error instanceof Error && error.name === "CodecError") {
    const fields = error as Error & { readonly code?: unknown; readonly path?: unknown; readonly codecId?: unknown };
    if (typeof fields.code === "string") {
      return new CodecError(fields.code as CodecErrorCode, typeof fields.path === "string" ? fields.path : "", error.message, typeof fields.codecId === "string" ? fields.codecId : undefined);
    }
  }
  return undefined;
}

/** RFC 6901 escaping for one path token. */
export function escapePointerToken(token: string): string {
  return token.replace(/~/g, "~0").replace(/\//g, "~1");
}

export function propertyPath(name: string): string {
  return "/" + escapePointerToken(name);
}

export function indexPath(index: number): string {
  return "/" + String(index);
}
