# Documenting the API

An ASP.NET Core API documents itself the way it would for OpenAPI — XML comments, endpoint summaries and descriptions,
`[Description]`, response descriptions, validation attributes — and Tisilia carries that text into the contract's
`documentation` (display text, outside the semantic hash). The Explorer shows it next to what it
describes (summaries in the rows, descriptions, parameter and response texts, a line per member in every schema), and the
generated client carries it as JSDoc, so editors show it on hover and in completion.

Nothing has to be registered: `tisilia export` reads it while it exports the operations.

## What is read

| target | from (the first that says something) | notes |
|---|---|---|
| operation | summary: `WithSummary(…)`, `[EndpointSummary]`, else the handler's XML `<summary>`; description: `WithDescription(…)`, `[EndpointDescription]`, else XML `<remarks>` | `[Obsolete]` on the handler (or the controller) adds **Deprecated.** first; the `[FromBody]` parameter's `[Description]` or `<param>` adds a **Request body** line |
| parameter (route, query, header) | `[Description]` on the parameter, else its XML `<param>`; for an `[AsParameters]` member or an MVC model property, the property's `[Description]` or XML comment | validation attributes become notes (below); `[Obsolete]` on the property adds **Deprecated.** |
| response case | `[ProducesResponseType(…, Description = …)]` / `Produces` metadata (what ApiExplorer reports), else the handler's `<response code="…">`, else `<returns>` for the case of the returned value | |
| type | `[Description]` on the type, else XML `<summary>`; `<remarks>` and `[Obsolete]` add to the description | records, classes, structs, enums, unions, paired types |
| member | `[Description]` on the property or field (or a record's constructor parameter), else the XML `<summary>` of the property / field / enum member, else the record's `<param>` | validation attributes and `[DefaultValue]` become notes; `[Obsolete]` adds **Deprecated.** |

An attribute wins over a comment. `TisiliaOptions.Documentation[targetId] = (summary, description)` replaces whatever was
gathered for that id.

Notes from validation attributes: `[Range]` (`1 ≤ value ≤ 100`, `<` where a bound is exclusive), `[StringLength]`,
`[Length]`, `[MinLength]`/`[MaxLength]` (characters for text, items for collections), `[RegularExpression]`,
`[AllowedValues]`/`[DeniedValues]`, `[EmailAddress]`, `[Url]`, `[Phone]`, `[CreditCard]`, `[Base64String]`; on members
also `[DefaultValue]`. They describe the API; whether the server enforces them is the application's choice
(`[ApiController]` validates automatically, a minimal API needs `AddValidation()` or its own checks) — the contract's
codecs do not change. A member's values are shown as System.Text.Json writes the member, the way ASP.NET Core's OpenAPI
writes `default` — an enum by its wire name (`[DefaultValue(Mood.VeryHappy)]` → `very-happy` under
`[JsonStringEnumMemberName("very-happy")]`), a string without its quotes; a parameter's by their C# names, which route,
query and header binding parses (`Enum.TryParse`, not the JSON options).

XML comments (`///`) are read from the documentation file the compiler writes next to each assembly, and the
`Kkdev92.Tisilia.AspNetCore` package turns that file on in the project that references it — there is nothing to set:

- a project that decides itself keeps its decision: a `GenerateDocumentationFile` or `DocumentationFile` of its own (in the
  project file or a `Directory.Build.props`) is left as it is, with the compiler's warnings, and
  `<GenerateDocumentationFile>false</GenerateDocumentationFile>` turns the file off;
- when the package turns it on, the build reports what it did before: no warning for a comment that is missing (CS1591,
  CS1573, CS1712), and one that is malformed — badly formed XML, an unknown `cref` or parameter name, a comment in the wrong
  place (CS1570, CS1572, CS1574, CS1587 …), whose text is then missing from the contract too — stays a warning, never an error
  under `TreatWarningsAsErrors`. As Microsoft documents, a documentation file also lets IDE0005 (unnecessary `using`) run on
  build where `EnforceCodeStyleInBuild` is on;
- a class library that holds handlers or types and does not reference the package needs
  `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in its own project file; its XML file is copied next to its
  assembly in the application's output, where the export finds it.

The package does this from `build/Kkdev92.Tisilia.AspNetCore.Documentation.props`, which the .NET SDK imports after the
project file and before it reads `GenerateDocumentationFile` (`BeforeMicrosoftNETSdkTargets`). The export reads the XML file
next to each assembly, `<inheritdoc/>` included; `<c>`, `<code>`, `<para>`, `<list>`, `<see>`, `<paramref>`, `<b>`, `<i>`,
`<a>` turn into Markdown. A lambda carries no XML comment (the compiler writes none for it): document a minimal API endpoint
with `WithSummary`/`WithDescription` and attributes on the lambda, or map a method that has the comment.

```csharp
/// <summary>A task.</summary>
/// <remarks>Tasks live in the API's memory.</remarks>
/// <param name="Title">What to do.</param>
public sealed record Todo(Guid Id, [property: StringLength(120, MinimumLength = 1)] string Title, Priority Priority);

app.MapGet("/todos/{id:guid}",
        [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, Description = "No task has that id.")]
        Results<Ok<Todo>, NotFound<ProblemDetails>> ([Description("The task's id.")] Guid id) => …)
   .WithSummary("Get a task")
   .WithTisiliaOperation("todos.get");
```

## In the contract

Entries are `{ targetId, summary, description }` (`contract.schema.json`), ordered by target id. A target is an id the
contract defines — an operation, a parameter (`<operation>.<name>`), a response case, a type — and each target has one
entry: the validator reports an entry for an unknown id (a renamed operation, a typo in a
`TisiliaOptions.Documentation` key) as SV03 and a second entry for one target as SV02.

Members and enum members have no ids, so a type's description ends with its member list — the line `**Members**`, then one
line per documented member, by the name the contract gives it (the wire name of an object member, the C# name of an enum
member):

```text
Tasks live in the API's memory.

**Members**

- `title` — What to do. Length 1–120 characters.
- `priority` — How soon it matters.
```

It reads as Markdown anywhere; the Explorer and the generator read it back per member. Text is Markdown: the Explorer
renders a safe subset (paragraphs, lists, code, emphasis, `http(s)` links) as text nodes, never as HTML.

Documentation is outside the semantic hash: changing it changes no case, no codec and no evidence. The generated client's
comments follow it, so `tisilia check` asks for a `generate` after a documentation change, like after any contract change.

## Deprecated

`[Obsolete]` is OpenAPI's `deprecated` (ASP.NET Core's OpenAPI maps it so for operations, schema types and schema
properties from .NET 11): the description of a deprecated operation, parameter or type, and a deprecated member's line,
starts with `**Deprecated.**` and the attribute's message. The contract has no other flag for it — like the rest of the
documentation it changes no case, codec or evidence, and the endpoint keeps answering. The Explorer strikes the path or name
through and says *deprecated* beside the operation or schema; the generated client marks the declaration `@deprecated`,
which TypeScript reports as a suggestion (not an error) that editors show by striking each use through.

```csharp
app.MapMethods("/http/todos/{id:guid}", ["HEAD"],
        [Obsolete("Use GET /http/todos/{id}: it answers 404 the same way and returns the task.")] Results<Ok, NotFound> (Guid id) => …)
   .WithSummary("Whether a task exists")
   .WithTisiliaOperation("http.todos.head", "http");
```

## Where it shows

- Explorer: an operation's summary in its row (two lines at most; the whole text as a tooltip) and its description when it is
  open; a parameter's text above its input and its notes below; a response case's text under its row; a body's type summary;
  in every schema a member's text and notes on its line, an enum member's on its own; the Schemas list with each type's
  summary and description; a deprecated operation, parameter, member or type struck through, and *deprecated* beside an
  operation or schema.
- Generated client: JSDoc on each model and member, enum member, argument type and parameter, and on each client method —
  summary, description, the documented response cases, then the route and operation id; `@deprecated` on what is deprecated
  (a deprecated enum's type and its const of known members both).
