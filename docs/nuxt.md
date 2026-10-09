# Nuxt integration

`@kkdev92/tisilia-nuxt` is the Nuxt 4 adapter. The core runtime stays free of Vue/Nuxt; the module only adds
request-scoped clients, allowlisted header forwarding, `useAsyncData` wrapping and hydration envelopes.

Finite binary downloads stay server-only in hydration. `useTisiliaOperation` may execute them during SSR and expose the full
decoded buffer through `serverResult`. Only a safe `failure/server-only` envelope enters the payload: no bytes, filename or raw
metadata. Hydration accepts that envelope without treating it as a mismatch or automatically refetching. Explicit refresh is a
new request; imperative `useTisiliaClient` calls can obtain bytes in the browser. Do not render `serverResult` into HTML. JSON
precision and browser-safe envelopes retain their existing behavior. The runtime, module and generator must come from the
same Tisilia version. The contract, hydration envelope format and Codec ABI all use version 0.1.

Finite raw uploads take a `Uint8Array` body. Their request identity includes the exact bytes as canonical base64 with
`bodyKind: "binary"`; different bytes, an empty body and an omitted body get different identities. A `ReadableStream` body,
which the runtime streams from Node, has no bytes before it is sent and so no identity: `useTisiliaOperation` refuses it. This identity input
is hashed, and the raw request bytes are not added to hydration envelopes. Use the runtime's `download` API with an
application-owned sink for incremental downloads; `useTisiliaOperation` retains its buffered response behavior.

## Install

```ts [nuxt.config.ts]
export default defineNuxtConfig({
  modules: ["@kkdev92/tisilia-nuxt"],
  tisilia: {
    forwardHeaders: ["authorization", "cookie", "accept-language"], // SSR only; credentials never leave the credential provider
  },
  runtimeConfig: {
    public: { tisilia: { baseUrl: "" } },       // NUXT_PUBLIC_TISILIA_BASE_URL — the one allowed browser origin
    tisilia: { serverBaseUrl: "", identityKey: "" }, // NUXT_TISILIA_SERVER_BASE_URL / NUXT_TISILIA_IDENTITY_KEY (server only)
  },
})
```

Only `hydration: "browser-safe-only"` and `sharedCache: false` exist; other values fail the build instead of being
downgraded.

`runtimeConfig` may name only the members an application sets (`{ public: { tisilia: { baseUrl } } }`): the module fills the
rest, and the resolved config (`useRuntimeConfig()`) has every member. The module declares its runtime config as object type
aliases because nuxt.config's `runtimeConfig` type (`Overrideable<RuntimeConfig>`) makes nested members optional only for types
that extend `Record<string, unknown>`, which an interface does not (`src/frontend/nuxt/test/config-types.ts`).

## Composables

| composable | purpose |
|---|---|
| `useTisiliaOperation(api, operation, args, options?)` | `useAsyncData` over one generated operation. The handler returns a **hydration envelope**; `result` decodes it with the case codec on both sides. |
| `useTisiliaClient(createXClient, overrides?)` | a request-scoped generated client for imperative calls (mutations). Built per call site; no global singleton captures a user's credentials. |
| `useTisiliaScope()` | the opaque scope nonce and `reset()` for login/logout/tenant changes (rotates the nonce and clears every `tisilia:` cache key). |
| `useTisiliaRequestScope()` | the per-render facts: base origin, forwarded headers, identity key. |

`api` is the generated client module's `{ semanticHash, apiId }`.

```vue
<script setup lang="ts">
import { semanticHash, apiId, usersGetOperation } from "./generated/index.js";
import type { UsersGetArgs, UsersGetResult } from "./generated/index.js";
const api = { semanticHash, apiId };
const user = await useTisiliaOperation<UsersGetArgs, UsersGetResult>(api, usersGetOperation, { id })
// user.envelope: the JSON-safe envelope in the payload; user.result: the typed result, the same on the server and in the
// browser (kind "response" | "hydration-failure" | "hydration-mismatch"); user.serverResult: during SSR only, the full outcome
</script>
```

## What travels in the payload

The payload holds `tisilia.hydration-envelope` records only: `json`/`text` envelopes carry `bodyText` (so int64,
decimal and 100 ns ticks are never re-parsed by a JSON number path), `bodyless` carries the case and status, and
`failure` carries a fixed code plus a safe message id — never a raw body, exception text or secret. Every envelope is
bound to `semanticHash`, `operationId`, the `requestIdentity` and the page's `scopeNonce`. Decoded BigInt/Decimal
values are never put into the payload; the browser decodes the envelope with the same case codec the server used.

A response case whose contract hydration is `server-only` (the exporter's default for statuses ≥ 400 and text
bodies, and always for XML bodies) hydrates as `failure/server-only`, and a failed call (timeout, contract mismatch, …) as a `failure` envelope
with its code. `result` is decoded from the envelope on both sides — `hydration-failure` with that code — so a page
renders the same thing on the server and in the browser and hydrates without a mismatch. Server code that needs the
body or the failure's details (to set the page's status, to log) reads `serverResult`, which exists during SSR only;
rendering from it would put into the HTML what the payload leaves out, and the browser could not hydrate it.

Hydration checks (schema, contract hash, operation, scope nonce, declared browser-safe case, status) happen before
decoding; a mismatch yields `{ kind: "hydration-mismatch" }` and Nuxt's normal client fetch takes over.

## Headers, credentials and origins

During SSR only the headers named in `forwardHeaders` are read from the incoming request. Hop-by-hop and
connection headers are never copied even when listed; credentials (`authorization`, `cookie`,
`proxy-authorization`) go through the runtime's credential provider, other headers must also be allowlisted by the
operation (`requestHeaderAllowlist`). Requests always go to the configured origin (`serverBaseUrl` on the server,
`baseUrl` in the browser); the runtime rejects redirects and absolute-URL injection.

In the browser (client navigation, `refreshNuxtData`, `lazy`/`server: false` operations) the page calls `baseUrl` itself:
when that is another origin than the Nuxt app's, the API needs CORS for the app's origin. With `semanticHashHeader` set,
the policy must also expose that header (ASP.NET Core: `WithExposedHeaders("x-tisilia-contract")`) — the browser hides
response headers CORS does not list, so the guard would find no hash and let every response through; in development the
module warns once (`[tisilia] the contract guard found no '…' header`). During SSR the server reads every header and the
guard always runs.

## Request identity and scope

The identity record is built from the prepared request (encoded path, query entries in send order, non-credential
allowlisted headers, body text, contract hash, scope nonce). Without forwarded credentials it is `rid:sha256:…`;
when credentials were forwarded on the server it is `rid:hmac-sha256:…` keyed with `runtimeConfig.tisilia.identityKey`
(≥ 32 bytes, base64 or UTF-8) or, when unset, a per-process random key. The browser never recomputes an identity;
it binds the envelope to the page's operation/arguments through the `useAsyncData` key, which is derived from the
canonical request without credentials.

The scope nonce is generated per SSR request, travels in the Nuxt state payload and gates every cache reuse:
`getCachedData` only returns an envelope whose nonce, contract hash and operation match. `useTisiliaScope().reset()`
after login/logout/tenant change rotates the nonce and clears the cached envelopes (`sharedCache` is always false).

Form requests use `bodyKind: "binary"` identities over the complete encoded bytes, including deterministic multipart
boundaries and file parts. Different field/file values produce different keys; credentials remain outside the record.
An XML request body is identified the same way, by the bytes of the document the client sends.
SSE responses are server-only for hydration, including finite event arrays. Subscribe explicitly in the consuming
application and stop the subscription when its scope ends. Endpoint-specific JSON responses retain their response
profile when decoded after hydration; request encoding continues to use the request profile.

## Tests

`src/frontend/nuxt/test/core.test.ts` covers header partitioning, request identity, cache keys and the hydration checks
without a Nuxt runtime, and `src/frontend/nuxt/test/config-types.ts` holds the runtime config types to what nuxt.config
accepts (`npm run typecheck`).
