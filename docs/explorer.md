# Tisilia Explorer

A static Vue 3 + Vite SPA (`src/frontend/explorer`) served by ASP.NET Core from `Tisilia.Explorer`
(`app.MapTisiliaExplorer()`, default route `/__tisilia/`). It does not need a Nuxt server, and it runs every request
through the same runtime pipeline as generated clients: `prepareRequest → fetchResponse → decodeResponse` over the
registry the runtime **interprets from the contract** (`createContractRegistry`), which builds exactly the codecs the
generator emits (same descriptors: presence per wire, name matching, duplicates, comparers, enum string form,
discriminators, nullable wrappers). There is no UI-only `JSON.stringify`/`JSON.parse` path.

## Routes

| route | content |
|---|---|
| `/__tisilia/` → `/__tisilia/index.html`, `/__tisilia/assets/*` | the embedded SPA (built by `npm run build -w src/frontend/explorer`; 503 with a problem when the bundle is missing) |
| `/__tisilia/contract` | the exported contract (`MapTisiliaContract`) |
| `/__tisilia/modules/{moduleId}/{artifact path}` | a module's **browser** artifact read from its declared path under the content root, served only when its bytes match the contract digest (409 otherwise) |

Everything sits behind the same guard as the contract: outside Development both `TisiliaOptions.AllowProduction`
and `AuthorizationPolicy` are required. Assets are sent with
`Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; …`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer` and `Cache-Control: no-store`. Modules are never
fetched from the contract or a remote origin.

Below a path the Explorer works as at the root: an application under a `PathBase` (IIS virtual application,
`X-Forwarded-Prefix`) or behind a proxy that strips a prefix before the application sees it. The route's redirect is
relative, and the page (which names its route in a `tisilia-explorer-route` meta element) calls the API at its own URL up
to that route — `https://host/app/__tisilia/` calls `https://host/app/items/1`, not `https://host/items/1`.

## Working with it

The page lists the operations the way API explorers usually do — grouped by tag, one row per operation with its method
colour, path and summary, opened in place — and keeps the steps few: inputs are editable at once, the response appears next
to them, and the search and *Authorize* stay in the top bar while scrolling.

- **Find**: the search matches tags, paths, methods, operation ids and summaries, word by word and ignoring case (`/`
  focuses it, the count shows how many operations match, `Enter` opens the first match, `Escape` clears). Rows show what
  matters before a call: a lock for `[Authorize]`/`RequireAuthorization()` (closed once credentials are set), server-only.
  Each operation has its own URL (`#/op/<id>`, nothing else goes into the URL), so it can be bookmarked and shared — *Copy
  link* in an open operation puts it on the clipboard.
- **Read**: an open operation shows the summary and description the API gives (its XML comments, `WithSummary`/`[Description]`
  and response descriptions, see [Documenting the API](api-documentation.md); text in Markdown), a parameter's text above its input and its
  notes (`[Range]`, `[StringLength]` …) below, a response case's text under its row, and a member's text on its line in
  every schema; what `[Obsolete]` marks is struck through, with *deprecated* beside an operation or schema; then its id,
  serializer profile, auth and antiforgery requirements and missing capabilities, the request
  body's shape (*Schema*) and the declared responses — one row per case with its status, case id and body type, opening to
  an example value or the
  schema (members, required/optional, null, formats such as "int64 as a JSON string", enum members, union variants by
  discriminator, recursion). *Schemas* at the end of the page lists every named model with its CLR type.
- **Fill in**: parameters grouped by location (path, query, headers), built from the request wire the server reads and the
  domain types: GUIDs get a generator, dates and times a "now", enums and flags their members, numbers keep the digits
  typed (never rounded through `number`), nullable values a null switch, lists add/remove. *Fill examples* gives every
  empty parameter an example of its kind. The body starts as an example of every field (format-correct for Version, IP
  address, URI …) and is edited as JSON (*Format* lays it out again) or as a form generated from the type (optional members
  add/remove, arrays and dictionaries add/remove/reorder, polymorphic bodies a variant picker that writes the
  discriminator). *Reset* brings the starting values back. Errors come back from the codecs at the field they name; a merely
  missing value is pointed out once the user executes or edits the field, and *Execute* then moves the focus to the first
  field that needs attention.
- **Execute**: *Execute* or `Ctrl`/`⌘ Enter`. Above the button the request line shows the URL the call will go to as the
  values change (or how many fields need attention), and *Headers and body this request sends* shows what the generated
  request encoder writes. The response appears beside the inputs on wide screens and below them on
  narrow ones (brought into view when it is out of sight): the status coloured by class, the declared case it decoded as
  (its row among the declared responses is marked), time and size, then the body as JSON or decoded (each value with its
  kind). Folded below are the response headers and the request: its URL and the call as code — through the generated
  client (`createXxxClient(...).operation({...})`, with the runtime helpers its values need), as `fetch`, or as `curl`, with
  the credentials that call went with named as placeholders. A failure says what it means and what to do (a 401/403
  offers *Authorize*, a 404 points to route constraints, a browser `Failed to fetch` to the console).
- **Large responses**: the JSON shows its first 300 lines and the rest on request; the decoded view opens
  three levels deep, lists 100 items and more in pages, and stops at 500 items (saying how many it left out) and at 24
  levels.
- **Language and look**: the Explorer speaks Japanese or English — the browser's first language of the two, else English —
  and its theme is the system's, light or dark; both are chosen in the top bar's display settings and remembered for the
  next visit. The API's own documentation, identifiers, types, HTTP reason phrases and codec details are shown as they are;
  an input error is worded by its kind, with the codec's detail after it. `<html lang>` follows the language (WCAG 3.1.1),
  and each language is named in itself. Each colour is a `light-dark()` pair resolved by the root's `color-scheme`; text
  colours meet WCAG AA contrast (4.5:1) in both schemes; the layout works down to phone width, and motion follows the
  reduced-motion setting. Markup and styles are Tisilia's own. The Explorer includes Vue and a small inline icon set
  adapted in part from Feather Icons. Both are MIT-licensed and documented in `NOTICE`. No icon font or remote icon asset
  is loaded at runtime.

## Typed and raw modes

- Typed: parameters are parsed by the parameter codecs' `parseRequestInput`; the body (the form's JSON AST, or the JSON
  editor's text parsed losslessly) goes to the body codec's `parseRequestInput`, then the generated request encoder writes
  the wire. Every codec the contract declares with a request-input capability has one, dictionaries and polymorphic types
  included. The preview (URL, headers, body, code) is the runtime's `PreparedRequest`; credentials never appear in it.
- Raw: the body text is sent verbatim (the URL/headers still come from the binders); the result is decoded by the
  same case codecs but the UI marks "generated request encoder not used" and makes no G2 claim (see
  [grades](conformance.md#grades)).

Decoded values are displayed as a tree that keeps int64, decimal (scale), 100 ns ticks and offsets as text; nothing
is rounded to a JavaScript number for display.

## Authorize

*Authorize* in the top bar (or the lock of a protected operation) opens a dialog for an `Authorization` header as
**Bearer** token or **Basic** user name and password (UTF-8, RFC 7617), any number of **API keys** (a header per key) and
**other credential headers** (a tenant, a CSRF token from the application). Header names and values are checked against
what a page may send (no `Cookie`, `Proxy-*` or `Sec-*`; visible ASCII values). The credentials reach every call through
the runtime's credential provider; the button then reads *Authorized* and the locks close. They live in this page's memory
only: never in browser storage, URLs, the contract or code — snippets name them as `$TOKEN`, `$API_KEY` … placeholders, and
a call keeps only which credential headers it went with, without values — and the request preview lists them as added
when sent. *Sign out* forgets the credentials together with every response; a reload forgets them too.

The host names the application's authentication schemes in a `tisilia-explorer-auth` meta element (scheme name, kind and
handler type from `IAuthenticationSchemeProvider`: JwtBearer and BearerToken are `bearer`, Cookies `cookie`, Negotiate and
Certificate are browser-managed), which the dialog shows and uses to pick its first tab. A cookie-authenticated API needs
no credentials here: the page is the application's own origin, so its calls carry the browser's cookies once the user is
signed in. When a response carries a token (`accessToken`, `access_token`, `token` … at its top level, as an Identity
`/login` answer does), the response offers *Authorize with it* — the value itself stays masked.

## Secrets and redaction

Redaction defaults to **mask**: leaves of the decoded tree, non-safe headers, the wire body, request bodies and code
snippets are masked until *Show values* (next to a response body, or in the request preview) shows them for this page;
*Hide values* masks them again. `authorization`, `cookie`, `set-cookie`, `x-api-key` and similar are never revealed. A
body that is not JSON cannot be masked field by field and shows only its byte count. The form's own inputs stay visible:
they are what the user is typing.

There is no history: an operation keeps its last response until the next call, *Clear* or *Sign out*. Copying follows the
screen: masked text copies directly; revealed text asks first, naming the clipboard as outside this page's redaction.
Copy always takes the whole text, also when only its first lines are shown. Browsers offer the
clipboard over https or on localhost only (an Explorer opened over plain http from a LAN address has none); a copy that
does not happen says why and shows the text. Browser storage holds the display settings alone — two keys in local storage,
`tisilia-explorer.locale` and `tisilia-explorer.theme`, each a value of a fixed list — and never a credential, a draft or a
response. The page renders text only: tests scan the sources for storage (`prefs.ts` alone may use local storage, and writes
only those two keys), `sessionStorage`, `indexedDB`, cookies, `v-html`, `innerHTML`, `eval` and inline `style` attributes
(which the CSP would refuse).

## Contract guard, cancellation, limits

Binary cases display media type, byte count and partial-response status without decoding or embedding HTML/SVG/PDF. Reveal
the response and explicitly confirm Save to download the received buffer. Explicit body masking prevents saving; an explicitly
hidden Content-Disposition cannot become a filename. Saving never repeats a POST. An object URL belongs to the current result
and is revoked on replacement, hiding, unmount and sign-out; a 206 is not offered as a complete file. Filename is advisory only.
The interpreter uses contract 0.1's resolved route plan and the same request writer as the generated client. Metadata and codec
qualification do not indicate that a route or download has been HTTP-tested in this deployment.

Requests carry the contract's `semanticHash` as `expectedSemanticHash` against the response header the host names in a
`tisilia-explorer-hash-header` meta element (`TisiliaOptions.SemanticHashHeader`, `x-tisilia-contract` by default),
compared only when the server emits it. Execution can be cancelled (`AbortController`); the runtime's default
limits, `redirect: error` and same-origin credentials apply, and `execute` accepts explicit `limits` (body bytes, depth,
tokens, number characters, timeout). A response beyond a limit is a `limit-failure` shown as data, never a partially
decoded value, and the status line (`executionStatus`) names the limit, a cancellation, a timeout, a transport or codec
failure, or a request that could not be sent at all.

## Bundling trusted modules: `tisilia explorer build`

`tisilia explorer build --registry <tisilia.explorer-registry.json> --allow-execute-build [--output <dir>]` produces a
static Explorer bundle that ships the browser artifacts of a **trusted registry** of codec modules, so a deployment
can serve the Explorer (and view a contract) without the ASP.NET module route and without ever fetching a module.

The registry's schema is `explorer-registry.schema.json` in `src/backend/Tisilia.Generator/Schemas`:

```json
{ "format": "tisilia.explorer-registry", "version": "0.1",
  "explorer": "node_modules/@kkdev92/tisilia-explorer",
  "contract": "tisilia.contract.json",
  "modules": [ { "manifest": "portable/generated/demo.portable.codec-manifest.json", "digest": "sha256:…", "root": "." } ] }
```

- Every manifest must have exactly the digest the registry trusts, and every browser artifact it declares must exist
  under `root` with the manifest's digest (SV44: pre-installed modules only). Any mismatch stops the
  command before anything runs (exit 3).
- `--allow-execute-build` is required because the Explorer package's `build` script (`vite build`) executes package
  code (exit 7 without it, nothing executed).
- Output: the SPA (`index.html`, `assets/`), `modules/<moduleId>/<artifact path>` (the same relative URLs the ASP.NET
  route serves), the optional `contract`, and `tisilia.explorer-bundle.json` — explorer name/version, registry digest,
  bundled modules with artifact digests and the digest of every file, so that evidence can bind to the bundle.
  A non-empty `--output` directory is never overwritten (exit 7).

## Tests

- `src/frontend/explorer/test/explorer.test.ts` — contract/module loading through the authorized routes, typed argument
  building and previews, typed/raw execution through the runtime pipeline with a stubbed `fetch`, display precision,
  header/value redaction (the response body as laid out, the code for a call and the credential placeholders it names),
  the decoded view's caps, nothing in browser storage but the display settings, nothing the CSP refuses.
- `src/frontend/explorer/test/core.test.ts` — forms and examples, scalar inputs, code snippets.
- `src/frontend/explorer/test/i18n.test.ts` — the language the browser prefers, stored choices (only known values; a storage
  that throws), every message in both languages, input errors and format hints in Japanese, and no words of the Explorer's
  own left in the templates.
- `src/frontend/explorer/test/docs.test.ts` — the Markdown subset documentation is shown in (HTML and other link schemes
  stay text), a type's member list read back (and a description that only uses its heading kept whole), the deprecation
  mark, entries by id.
- `src/frontend/explorer/test/notice.test.ts` — bundled package licenses (from the build's list) and vendored material
  (from `third-party-sources.json`) are covered by both the root and Explorer npm package `NOTICE`, including copyright
  and license text. `scripts/install-check.ps1` also checks that notices ship in the npm and NuGet archives.
