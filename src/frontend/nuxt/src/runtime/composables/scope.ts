import { clearNuxtData, useState } from "nuxt/app";
import { createScopeNonce } from "@kkdev92/tisilia-runtime";
import type { Ref } from "vue";

export const scopeStateKey = "tisilia:scope-nonce";
export const asyncDataKeyPrefix = "tisilia:";

export interface TisiliaScope {
  /** Opaque ≥128-bit public nonce bound to the current user/tenant scope. */
  readonly nonce: Ref<string>;
  /** Rotates the nonce and discards every cached envelope: call on login, logout and tenant changes. */
  reset(): void;
}

export function useTisiliaScope(): TisiliaScope {
  const nonce = useState<string>(scopeStateKey, () => createScopeNonce());
  return {
    nonce,
    reset() {
      nonce.value = createScopeNonce();
      clearNuxtData((key) => key.startsWith(asyncDataKeyPrefix));
    },
  };
}
