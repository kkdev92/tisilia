import { ordinalUpper } from "../primitives/ordinalCasing.js";

/**
 * TisiliaMap<K, V>: a dictionary whose key identity is defined by the key codec's canonical string
 * and the contract's comparer, never by JavaScript Map's SameValueZero. Insertion order is preserved.
 */

export type KeyComparer = "ordinal" | "ordinal-ignore-case" | "structural";

export function normalizeKey(comparer: KeyComparer, encoded: string): string {
  switch (comparer) {
    case "ordinal":
    case "structural":
      return encoded;
    case "ordinal-ignore-case":
      // .NET's StringComparer.OrdinalIgnoreCase, not JavaScript's full case mapping ("ß" and "SS" are different keys in .NET)
      return ordinalUpper(encoded);
  }
}

export class TisiliaMap<K, V> {
  private readonly slots = new Map<string, { key: K; value: V }>();

  constructor(
    readonly comparer: KeyComparer,
    private readonly encodeKey: (key: K) => string,
  ) {}

  /**
   * A map for a request, keyed structurally (equal key values are one entry). The contract's map codec re-encodes every key with its key
   * codec and rejects keys that collide under the contract's comparer when the request is encoded, so no key encoder is needed here.
   */
  static of<K, V>(entries: Iterable<readonly [K, V]> = []): TisiliaMap<K, V> {
    return TisiliaMap.from<K, V>("structural", structuralKey, entries);
  }

  static from<K, V>(comparer: KeyComparer, encodeKey: (key: K) => string, entries: Iterable<readonly [K, V]>): TisiliaMap<K, V> {
    const map = new TisiliaMap<K, V>(comparer, encodeKey);
    for (const [k, v] of entries) {
      map.set(k, v);
    }
    return map;
  }

  private slot(key: K): string {
    return normalizeKey(this.comparer, this.encodeKey(key));
  }

  get size(): number {
    return this.slots.size;
  }

  has(key: K): boolean {
    return this.slots.has(this.slot(key));
  }

  /** True when a key with the same encoded form already exists (used to detect collisions before sending). */
  hasEncoded(encoded: string): boolean {
    return this.slots.has(normalizeKey(this.comparer, encoded));
  }

  get(key: K): V | undefined {
    return this.slots.get(this.slot(key))?.value;
  }

  set(key: K, value: V): this {
    const s = this.slot(key);
    const existing = this.slots.get(s);
    if (existing !== undefined) {
      existing.value = value;
    } else {
      this.slots.set(s, { key, value });
    }
    return this;
  }

  delete(key: K): boolean {
    return this.slots.delete(this.slot(key));
  }

  clear(): void {
    this.slots.clear();
  }

  *entries(): IterableIterator<[K, V]> {
    for (const { key, value } of this.slots.values()) {
      yield [key, value];
    }
  }

  *keys(): IterableIterator<K> {
    for (const { key } of this.slots.values()) {
      yield key;
    }
  }

  *values(): IterableIterator<V> {
    for (const { value } of this.slots.values()) {
      yield value;
    }
  }

  [Symbol.iterator](): IterableIterator<[K, V]> {
    return this.entries();
  }

  forEach(callback: (value: V, key: K, map: TisiliaMap<K, V>) => void): void {
    for (const [k, v] of this.entries()) {
      callback(v, k, this);
    }
  }
}

/** A deterministic string for a key value: type-tagged scalars, bigint, bytes and plain objects (property order as given). */
function structuralKey(key: unknown): string {
  if (typeof key === "string") {
    return "s:" + key;
  }
  if (typeof key === "number") {
    // JSON.stringify writes NaN, Infinity and -Infinity all as null; -0 and 0 are one key, as in a .NET Dictionary<double, …>
    return "d:" + (Object.is(key, -0) ? "0" : String(key));
  }
  return JSON.stringify(key, (_, v: unknown) => (typeof v === "bigint" ? "n:" + v.toString() : v instanceof Uint8Array ? Array.from(v) : v)) ?? String(key);
}
