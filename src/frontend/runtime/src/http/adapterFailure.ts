const brand = Symbol.for("@kkdev92/tisilia-runtime/adapter-failure");

/**
 * A failure of the application's own fetch adapter (`transport.fetch`) that the caller gets back as it is. A fetch adapter that
 * sends requests through the application's own stack — token refresh, retries, its own typed errors — throws
 * `new AdapterFailure(cause)`; the operation's result is then a transport failure with reason `"adapter"` whose `cause` is that
 * value, which the runtime never reads, copies or puts into a hydration envelope. Anything else a fetch adapter throws is
 * classified with fixed text, since it may carry request headers or bodies. Recognized across copies of the runtime.
 */
export class AdapterFailure extends Error {
  readonly [brand] = true;

  constructor(cause: unknown) {
    super("the fetch adapter failed the request", { cause });
    this.name = "AdapterFailure";
  }
}

/** Whether a thrown value is an {@link AdapterFailure}, of this copy of the runtime or another. */
export function isAdapterFailure(value: unknown): value is AdapterFailure {
  try {
    return typeof value === "object" && value !== null && (value as { readonly [brand]?: unknown })[brand] === true;
  } catch {
    return false;
  }
}
