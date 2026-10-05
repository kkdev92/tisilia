/** One cooperative epoch-millisecond budget across preparation, provider, I/O and decoding. */
export class BudgetEnded extends Error {
  constructor(readonly kind: "cancelled" | "timeout") { super(kind); }
}

export class ExecutionBudget {
  readonly deadline: number;
  readonly signal: AbortSignal;
  private readonly controller = new AbortController();
  private readonly stopped: Promise<void>;
  private wake!: () => void;
  private terminal?: BudgetEnded;
  private timer?: ReturnType<typeof setTimeout>;
  private readonly abort = (): void => { this.stop("cancelled"); };

  constructor(readonly timeoutMs: number, private readonly userSignal?: AbortSignal, readonly now: () => number = Date.now) {
    this.deadline = now() + timeoutMs;
    this.signal = this.controller.signal;
    this.stopped = new Promise<void>((resolve) => { this.wake = resolve; });
    if (userSignal?.aborted) { this.stop("cancelled"); }
    else {
      userSignal?.addEventListener("abort", this.abort, { once: true });
      this.timer = setTimeout(() => this.stop("timeout"), timeoutMs);
    }
  }

  private stop(kind: "cancelled" | "timeout"): void {
    if (this.terminal !== undefined) { return; }
    this.terminal = new BudgetEnded(kind);
    this.wake();
    this.controller.abort();
  }

  check(): void {
    if (this.terminal === undefined) {
      if (this.userSignal?.aborted) { this.stop("cancelled"); }
      else if (this.now() >= this.deadline) { this.stop("timeout"); }
    }
    if (this.terminal !== undefined) { throw this.terminal; }
  }

  async wait<T>(start: () => T | PromiseLike<T>): Promise<T> {
    this.check();
    // Both branches attach rejection handlers even when an uncooperative source settles after the deadline.
    const result = await Promise.race([
      Promise.resolve().then(() => { this.check(); return start(); }).then(value => ({ done: true as const, value })),
      this.stopped.then(() => ({ done: false as const })),
    ]);
    if (!result.done) { throw this.terminal!; }
    this.check();
    return result.value;
  }

  dispose(): void {
    if (this.timer !== undefined) { clearTimeout(this.timer); }
    this.userSignal?.removeEventListener("abort", this.abort);
  }
}

/** Cleanup is best-effort: its failure or non-completion cannot replace the primary outcome. */
export function discard(start: () => unknown): void {
  try { void Promise.resolve(start()).catch(() => {}); } catch { /* preserve primary failure */ }
}
