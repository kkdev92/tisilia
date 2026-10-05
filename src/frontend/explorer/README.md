# @kkdev92/tisilia-explorer

<!-- Tisilia artwork: enable the package banner after an anonymous public image URL is verified. -->

The source package includes the two JPEGs used in the empty state and a self-contained TypeScript configuration, so it can be rebuilt outside the repository. In the repository, `npm run brand:sync` copies the selected artwork from `assets/brand/tisilia`; `npm run brand:check` verifies it. Neither command is a package build or prepack hook.

The Tisilia Explorer: a Vue + Vite single-page application that lists an API's operations from its contract and runs them
through the same runtime codecs as the generated clients. This package is the bundle source:
`dotnet tisilia explorer build --registry <file> --allow-execute-build` bundles it with the application's trusted codec
modules, and `Kkdev92.Tisilia.Explorer` serves the built SPA from an authorized ASP.NET Core route (`MapTisiliaExplorer`).

What it offers: the operations by tag, opened in place with the request and the response side by side, with the API's own
documentation (summaries, descriptions, parameter, response and member texts and validation notes, in Markdown; what
`[Obsolete]` marks struck through); inputs generated from the contract and editable at once (typed controls, examples, a
JSON or form editor for the body, errors at the field the codec names, focus on the first one that needs attention); the
URL a call will go to as the values change; responses
with their declared case, the lossless wire JSON or the decoded value, headers, and the call as generated-client, `fetch`
and `curl` code; an Authorize dialog for Bearer, Basic, API keys and other credential headers (memory only, a token from a
login response one click away); search with `/` and `Enter`; the schemas; Japanese or English and the system's, light or
dark theme, chosen in the top bar and remembered. Response values stay masked until shown for the session, and browser
storage holds the display settings alone.

Details: https://github.com/kkdev92/tisilia/blob/main/docs/explorer.md

The source code is [MIT-licensed](./LICENSE). The bundled Tisilia-chan JPEGs are copies of the repository's brand artwork
and have separate branding and identity usage guidance in the [Tisilia Brand Asset Policy](./public/BRAND-ASSET-POLICY.md).
The policy permits normal factual, editorial, documentation, and integration references, without granting endorsement
or trademark rights. The build carries this policy into the distribution as `BRAND-ASSET-POLICY.md`.
Third-party attribution remains in [NOTICE](./NOTICE).
