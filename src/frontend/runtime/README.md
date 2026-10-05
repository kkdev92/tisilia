# @kkdev92/tisilia-runtime

<!-- Tisilia artwork: enable the package banner after an anonymous public image URL is verified. -->

The runtime of the TypeScript clients Tisilia generates from an ASP.NET Core API: a lossless JSON parser and writer, exact
primitives for the .NET types JavaScript numbers cannot hold (`Int64`, `UInt64`, `Decimal`, `Guid`, `DateOnly`, `TimeOnly`,
`DateTimeOffset`, `Duration`, …), the codecs a generated client is built from, and an HTTP pipeline that classifies every
outcome instead of throwing.

```text
npm install @kkdev92/tisilia-runtime@<version>   # the version of the CLI that generated the client: dotnet tisilia version
```

```ts
import { formatDateOnly } from "@kkdev92/tisilia-runtime";
import { createWeatherApiClient } from "./api/index.js"; // written by `dotnet tisilia generate`

const client = createWeatherApiClient({ baseUrl: "https://localhost:7001" });
const result = await client.weatherForecast();
if (result.kind === "response") {
  for (const forecast of result.data) {
    if (forecast !== null) console.log(formatDateOnly(forecast.date), forecast.summary);
  }
} else {
  console.error(result.kind); // unexpected-response, codec-failure, transport-failure, timeout, limit-failure, …
}
```

A call resolves to a declared response case (`kind: "response"`, `caseId`, `status`, `data`) or to one of the failure
kinds; responses are read with byte, depth, token and number-length limits (`ClientOptions.limits`). Redirects are never
followed. Node's fetch says so (`transport-failure`, `reason: "redirect"`); browsers report a redirect, a CORS refusal and an
unreachable server alike (`reason: "network"`, "Failed to fetch"), and only their developer console names the cause.

Getting started (server registration, `export`, `init`, `generate`):
https://github.com/kkdev92/tisilia/blob/main/docs/getting-started.md

Requires Node 24 and TypeScript 6 to build with; in the browser, the current versions of Chrome, Edge, Firefox and Safari.
MIT license.
