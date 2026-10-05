import { useAsyncData, useNuxtApp } from "nuxt/app";
import type { AsyncData } from "nuxt/app";
import type { ClientOptions, HydrationEnvelope, OperationDescriptor, OperationResult, PreparedRequest } from "@kkdev92/tisilia-runtime";
import { CodecError, createHydrationEnvelope, executeWithRaw, failureEnvelopeOf, prepareRequest } from "@kkdev92/tisilia-runtime";
import { computed, toValue, type ComputedRef, type MaybeRefOrGetter, type MultiWatchSources } from "vue";
import { asyncDataKeyOf, envelopeMatchesScope, hiddenGuardHeader, hydrate, identityOf, identityRecordOf, type HydratedResult } from "../core.js";
import { clientOptionsFor, useTisiliaRequestScope } from "./client.js";
import { useTisiliaScope } from "./scope.js";

/** What the generated client module exports: the contract identity the envelopes are bound to. */
export interface TisiliaApi {
  readonly semanticHash: string;
  readonly apiId?: string;
}

export interface UseTisiliaOperationOptions {
  /** Explicit useAsyncData key; the default derives from the canonical request (never from credentials). */
  readonly key?: string;
  readonly server?: boolean;
  readonly lazy?: boolean;
  readonly immediate?: boolean;
  readonly watch?: MultiWatchSources;
  readonly client?: Partial<ClientOptions>;
}

export interface TisiliaOperationData<TResult extends OperationResult> {
  /** The hydration envelope (JSON-safe control data + bodyText) that travels in the Nuxt payload. */
  readonly envelope: AsyncData<HydrationEnvelope | undefined, unknown>["data"];
  /**
   * The typed result decoded from the envelope with the case codec; `undefined` until an envelope exists. The same on the
   * server and in the browser, so what a page renders from it hydrates without a mismatch: a failure or a server-only case
   * is `hydration-failure` with the envelope's code on both sides.
   */
  readonly result: ComputedRef<HydratedResult<TResult> | undefined>;
  /**
   * During SSR only: the full outcome behind the envelope — a server-only body, the failure's details — for decisions the
   * server makes (the page's response status, logging). `undefined` in the browser: rendering from it would put what the
   * envelope deliberately leaves out into the HTML, and the browser could not hydrate it.
   */
  readonly serverResult: ComputedRef<TResult | undefined>;
  readonly status: AsyncData<HydrationEnvelope | undefined, unknown>["status"];
  readonly pending: AsyncData<HydrationEnvelope | undefined, unknown>["pending"];
  readonly error: AsyncData<HydrationEnvelope | undefined, unknown>["error"];
  readonly refresh: AsyncData<HydrationEnvelope | undefined, unknown>["refresh"];
  readonly execute: AsyncData<HydrationEnvelope | undefined, unknown>["execute"];
  readonly clear: AsyncData<HydrationEnvelope | undefined, unknown>["clear"];
}

// Server-side: the raw outcome behind an envelope, for serverResult (server-only or failure details the envelope
// deliberately does not carry). Never serialized.
const serverOutcomes = new WeakMap<object, OperationResult>();

// Development, browser: guard headers already reported as hidden by CORS (once per page load).
const warnedGuardHeaders = new Set<string>();

/**
 * useAsyncData over one operation: the handler returns a hydration envelope, the payload carries
 * `bodyText` (no BigInt/Decimal in the payload), and the browser decodes with the same case codec after checking the
 * contract hash, operation, scope nonce and declared case. Server-only cases hydrate as `failure/server-only`.
 */
export function useTisiliaOperation<TArgs, TResult extends OperationResult = OperationResult>(
  api: TisiliaApi,
  operation: OperationDescriptor,
  args: MaybeRefOrGetter<TArgs>,
  options: UseTisiliaOperationOptions = {},
): TisiliaOperationData<TResult> & Promise<TisiliaOperationData<TResult>> {
  const nuxtApp = useNuxtApp();
  const scope = useTisiliaScope();
  const requestScope = useTisiliaRequestScope();
  const clientOptions = clientOptionsFor(requestScope, operation, {
    ...(requestScope.semanticHashHeader !== undefined ? { expectedSemanticHash: api.semanticHash } : {}),
    ...options.client,
  });
  const expected = () => ({ semanticHash: api.semanticHash, operationId: operation.id, scopeNonce: scope.nonce.value });

  const prepare = (): PreparedRequest | CodecError => {
    try {
      return prepareRequest(operation, toValue(args), clientOptions);
    } catch (error) {
      if (error instanceof CodecError) {
        return error;
      }
      throw error;
    }
  };

  const key = computed(() => {
    if (options.key !== undefined) {
      return options.key;
    }
    const prepared = prepare();
    return prepared instanceof CodecError ? `tisilia:${operation.id}:invalid:${prepared.code}` : asyncDataKeyOf(identityRecordOf(operation, prepared, api.semanticHash, scope.nonce.value));
  });

  const asyncData = useAsyncData<HydrationEnvelope | undefined>(
    key,
    async (_app, { signal }) => {
      const nonce = scope.nonce.value;
      const context = { semanticHash: api.semanticHash, operationId: operation.id, scopeNonce: nonce };
      const prepared = prepare();
      if (prepared instanceof CodecError) {
        return failureEnvelopeOf({ ...context, requestIdentity: "rid:sha256:" + "0".repeat(64) }, { kind: "transport-failure", operationId: operation.id, reason: "request-encoding", message: prepared.code });
      }
      const record = identityRecordOf(operation, prepared, api.semanticHash, nonce);
      const requestIdentity = await identityOf(record, requestScope.forwarded.credentials.length > 0, requestScope.identityKey);
      const { raw, result } = await executeWithRaw(operation, toValue(args), { ...clientOptions, signal }, prepared);
      if (import.meta.dev && import.meta.client) {
        const hidden = hiddenGuardHeader(raw, requestScope.semanticHashHeader);
        if (hidden !== undefined && !warnedGuardHeaders.has(requestScope.semanticHashHeader!)) {
          warnedGuardHeaders.add(requestScope.semanticHashHeader!);
          console.warn(hidden);
        }
      }
      const envelope = createHydrationEnvelope(operation, raw, { ...context, requestIdentity });
      if (import.meta.server) {
        serverOutcomes.set(envelope, result);
      }
      return envelope;
    },
    {
      ...(options.server !== undefined ? { server: options.server } : {}),
      ...(options.lazy !== undefined ? { lazy: options.lazy } : {}),
      ...(options.immediate !== undefined ? { immediate: options.immediate } : {}),
      ...(options.watch !== undefined ? { watch: options.watch } : {}),
      // Nuxt's default lookup (payload while hydrating, static data otherwise; nuxt v4.5.2 asyncData.ts) plus the
      // A cached envelope is reused only inside the same scope and contract (sharedCache=false)
      getCachedData: (k, app, ctx) => {
        if (ctx.cause === "refresh:manual" || ctx.cause === "refresh:hook") {
          return undefined;
        }
        const cached: unknown = app.isHydrating ? app.payload.data[k] : app.static.data[k];
        return envelopeMatchesScope(cached, expected()) ? cached : undefined;
      },
    },
  );

  // from the envelope on both sides: the browser hydrates exactly what the server rendered
  const result = computed<HydratedResult<TResult> | undefined>(() => {
    const envelope = asyncData.data.value;
    if (envelope === undefined || envelope === null) {
      return undefined;
    }
    return hydrate<TResult>(operation, envelope, expected(), clientOptions);
  });

  const serverResult = computed<TResult | undefined>(() => {
    const envelope = asyncData.data.value;
    if (!import.meta.server || envelope === undefined || envelope === null) {
      return undefined;
    }
    return serverOutcomes.get(envelope) as TResult | undefined;
  });

  const data: TisiliaOperationData<TResult> = {
    envelope: asyncData.data,
    result,
    serverResult,
    status: asyncData.status,
    pending: asyncData.pending,
    error: asyncData.error,
    refresh: asyncData.refresh,
    execute: asyncData.execute,
    clear: asyncData.clear,
  };
  const promise = asyncData.then(() => data) as Promise<TisiliaOperationData<TResult>>;
  void nuxtApp;
  return Object.assign(promise, data);
}
