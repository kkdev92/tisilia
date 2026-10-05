# @kkdev92/tisilia-nuxt

<!-- Tisilia artwork: enable the package banner after an anonymous public image URL is verified. -->

The Nuxt 4 module for Tisilia clients: request-scoped clients (no global singleton holds a user's credentials), allowlisted
header forwarding during SSR, and `useAsyncData` over hydration envelopes that keep exact numbers and declared response cases
from the server render to the browser.

```text
npm install @kkdev92/tisilia-nuxt@<version> @kkdev92/tisilia-runtime@<version>   # the version of the CLI: dotnet tisilia version
```

```ts
// nuxt.config.ts
export default defineNuxtConfig({
  modules: ["@kkdev92/tisilia-nuxt"],
  tisilia: { forwardHeaders: ["authorization", "cookie", "accept-language"] },
  runtimeConfig: {
    public: { tisilia: { baseUrl: "" } },              // NUXT_PUBLIC_TISILIA_BASE_URL
    tisilia: { serverBaseUrl: "", identityKey: "" },   // NUXT_TISILIA_SERVER_BASE_URL / NUXT_TISILIA_IDENTITY_KEY
  },
});
```

```vue
<script setup lang="ts">
// app/api: the client `dotnet tisilia generate` wrote
import { semanticHash, apiId, usersGetOperation } from "~/api/index.js";
import type { UsersGetArgs, UsersGetResult } from "~/api/index.js";

const api = { semanticHash, apiId }; // the contract every envelope is bound to
const user = await useTisiliaOperation<UsersGetArgs, UsersGetResult>(api, usersGetOperation, { id });
// user.result: the typed result, the same during SSR and in the browser (render from it);
// user.serverResult: the full outcome behind a failure or a server-only case, during SSR only (status, logging)
</script>
```

Composables: `useTisiliaOperation`, `useTisiliaClient`, `useTisiliaScope`, `useTisiliaRequestScope`. Details:
https://github.com/kkdev92/tisilia/blob/main/docs/nuxt.md

Requires Nuxt 4.5 or later. MIT license.
