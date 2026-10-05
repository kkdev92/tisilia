import type { Codec } from "./abi.js";
import { CodecError } from "./errors.js";

/**
 * Execution registry: binds codec ids to locally installed implementations. Nothing is loaded from a
 * URL. Registration is explicit; duplicates for the same id are rejected so that no scope is claimed twice.
 */
export class CodecRegistry {
  private readonly codecs = new Map<string, Codec<unknown>>();
  private readonly lazy = new Map<string, () => Codec<unknown>>();

  register(codec: Codec<unknown>): this {
    if (this.codecs.has(codec.id) || this.lazy.has(codec.id)) {
      throw new Error(`codec '${codec.id}' is already registered`);
    }
    this.codecs.set(codec.id, codec);
    return this;
  }

  registerLazy(id: string, factory: () => Codec<unknown>): this {
    if (this.codecs.has(id) || this.lazy.has(id)) {
      throw new Error(`codec '${id}' is already registered`);
    }
    this.lazy.set(id, factory);
    return this;
  }

  has(id: string): boolean {
    return this.codecs.has(id) || this.lazy.has(id);
  }

  get<T = unknown>(id: string): Codec<T> {
    let codec = this.codecs.get(id);
    if (codec === undefined) {
      const factory = this.lazy.get(id);
      if (factory === undefined) {
        throw new CodecError("unsupported", "", `codec '${id}' is not registered`, id);
      }
      codec = factory();
      this.codecs.set(id, codec);
      this.lazy.delete(id);
    }
    return codec as Codec<T>;
  }

  ids(): string[] {
    return [...this.codecs.keys(), ...this.lazy.keys()].sort();
  }
}
