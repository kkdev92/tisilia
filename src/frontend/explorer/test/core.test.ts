import { readFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { describe, expect, it } from "vitest";
import { createCodecContext, decimalFromString, parseDateOnly, parseJson, prepareRequest, TisiliaMap, writeJson, type JsonValue } from "@kkdev92/tisilia-runtime";
import { credentialHeaders, credentialProblem, parseAuthHints, tokenField } from "../src/auth.js";
import { buildArgs, documentationOf, filterOperations, loadExplorer, summarize, type ExplorerModel } from "../src/explorer.js";
import { cyclic, errorsByPath, exampleOf, FormSchema, grammarKind, nowText, prettyJson, sanitize, type InputNode } from "../src/forms.js";
import { highlightJson } from "../src/highlight.js";
import { clientFactoryName, clientSnippet, curlSnippet, fetchSnippet, operationMethodName, tsLiteral } from "../src/snippets.js";

const fixtures = fileURLToPath(new URL("../../../../tests/fixtures/", import.meta.url));

async function load(): Promise<ExplorerModel> {
  return loadExplorer({
    baseHref: "http://app.test/__tisilia/",
    fetchImpl: (async () => new Response(readFileSync(fixtures + "minimal-api.contract.json", "utf8"), { status: 200 })) as typeof fetch,
    importImpl: async (url) => (await import(pathToFileURL(fixtures + (url.includes("demo.money") ? "modules/demo.money/money.js" : "modules/demo.portable/demo.portable.portable.js")).href)) as Record<string, unknown>,
  });
}

const fixed = { now: new Date(2026, 9, 2, 13, 45, 30), uuid: () => "550e8400-e29b-41d4-a716-446655440000" };

describe("request forms", () => {
  it("builds every operation's form from its request wire, and every example passes the request-input codecs", async () => {
    const model = await load();
    const schema = new FormSchema(model.document);
    let bodies = 0;
    for (const op of model.operations) {
      if (op.requestBody.kind !== "json") {
        continue;
      }
      bodies++;
      const example = sanitize(exampleOf(schema.body(op.requestBody.use), fixed));
      const built = buildArgs(model, op, { parameters: {}, body: "", bodyJson: example });
      expect(built.errors.filter((e) => e.path.startsWith("/body")), op.id + " " + writeJson(example)).toEqual([]);
    }
    expect(bodies).toBe(7);
  });

  it("picks controls from wire grammars and domain types", async () => {
    const model = await load();
    const schema = new FormSchema(model.document);
    const op = (id: string): InputNode => {
      const o = model.operations.find((x) => x.id === id)!;
      return schema.body((o.requestBody as Extract<typeof o.requestBody, { kind: "json" }>).use);
    };
    const user = op("users.put");
    expect(user.kind).toBe("object");
    const props = Object.fromEntries((user as Extract<InputNode, { kind: "object" }>).properties.map((p) => [p.name, p]));
    // int64 read from a number or a string: one number field
    expect(props["revision"]!.node).toMatchObject({ kind: "scalar", widget: "integer", token: "number" });
    expect(props["nickname"]!.node).toMatchObject({ kind: "nullable", inner: { kind: "scalar", widget: "text" } });
    expect(props["revision"]!.required).toBe(false);
    const shape = op("portable.shape");
    expect(shape).toMatchObject({ kind: "tagged", discriminator: "kind" });
    expect((shape as Extract<InputNode, { kind: "tagged" }>).variants.map((v) => v.label)).toEqual(["circle · Circle", "rect · Rect"]);
    const invoice = op("portable.invoice") as Extract<InputNode, { kind: "object" }>;
    const widget = (name: string): string => {
      const node = invoice.properties.find((p) => p.name === name)!.node;
      return node.kind === "scalar" ? node.widget : node.kind;
    };
    expect(["id", "issued", "due", "at", "stamp", "local", "localWire", "period", "ratio", "payload", "initial"].map(widget)).toEqual(["guid", "datetime-offset", "date", "time", "datetime-utc", "datetime", "datetime-offset", "duration", "float", "bytes", "char"]);
    // a recursive type: finite example, and optional self-references are left out
    const tree = op("portable.tree");
    expect(cyclic(tree)).toBe(true);
    expect(writeJson(sanitize(exampleOf(tree, fixed))).length).toBeLessThan(2000);
    expect(grammarKind("tisilia-additional.grammar.half-string-named")).toBe("half");
    expect(grammarKind("tisilia.grammar.date-only-key@0.1")).toBe("date-only");
    // an enum's choices carry the member's integer, the domain value a server default names (shown as "Public (1)")
    const visibility = schema.parameter({ typeId: "sample-api.Tisilia.Samples.MinimalApi.Visibility.number", codecId: "", semanticNullable: false });
    expect(visibility.choices?.map((c) => [c.label, c.member, c.number])).toEqual([
      ["Public (1)", "Public", "1"],
      ["Private (2)", "Private", "2"],
    ]);
  });

  it("keeps number lexemes, turns non-numbers into strings for the codec to name, and maps codec errors to fields", async () => {
    expect(prettyJson(parseJson('{"a":9007199254740993,"b":1.10,"c":[]}'))).toBe('{\n  "a": 9007199254740993,\n  "b": 1.10,\n  "c": []\n}');
    expect(writeJson(sanitize({ kind: "object", entries: [{ name: "n", value: { kind: "number", text: "12a" } }] }))).toBe('{"n":"12a"}');
    const model = await load();
    const op = model.operations.find((o) => o.id === "portable.money")!;
    const built = buildArgs(model, op, { parameters: {}, body: "", bodyJson: parseJson('{"amount":"x","currency":"JPY"}') });
    expect([...errorsByPath(built.errors).keys()]).toEqual(["/amount"]);
    expect(nowText("datetime-offset", new Date(2026, 9, 2, 13, 45, 30))).toMatch(/^2026-10-02T13:45:30[+-]\d\d:\d\d$/);
  });

  it("reads nested portable types with their request program (a decimal the request wire also reads from a number)", async () => {
    const model = await load();
    const op = model.operations.find((o) => o.id === "portable.invoice")!;
    const schema = new FormSchema(model.document);
    const example = sanitize(exampleOf(schema.body((op.requestBody as Extract<typeof op.requestBody, { kind: "json" }>).use), fixed)) as Extract<JsonValue, { kind: "object" }>;
    const withTotal = (amount: JsonValue): JsonValue => ({ kind: "object", entries: example.entries.map((e) => (e.name === "total" ? { name: "total", value: { kind: "object", entries: [{ name: "amount", value: amount }, { name: "currency", value: { kind: "string", value: "JPY" } }] } } : e)) });
    expect(buildArgs(model, op, { parameters: {}, body: "", bodyJson: withTotal({ kind: "number", text: "12.5" }) }).errors).toEqual([]);
    expect(buildArgs(model, op, { parameters: {}, body: "", bodyJson: withTotal({ kind: "string", value: "12.5" }) }).errors).toEqual([]);
  });

  it("takes dictionary input through the contract interpreter (the contract declares it for map codecs)", async () => {
    const model = await load();
    const codec = model.registry.registry.get("sample-api.System.Collections.Generic.Dictionary_System.String_System.Int64_.response.codec");
    const map = codec.parseRequestInput!(parseJson('{"a":"9007199254740993","b":2}'), createCodecContext()) as TisiliaMap<string, bigint>;
    expect([...map.entries()]).toEqual([["a", 9007199254740993n], ["b", 2n]]);
  });
});

describe("catalog", () => {
  it("summarizes documentation and auth, and searches every word", async () => {
    const model = await load();
    const summaries = summarize(model);
    expect(summaries.find((s) => s.id === "users.get")?.authRequired).toBe(false);
    expect(filterOperations(summaries, "users put", undefined).map((s) => s.id)).toEqual(["users.put"]);
    expect(filterOperations(summaries, "post portable", undefined).map((s) => s.id)).toEqual(["portable.money", "portable.shape", "portable.invoice", "portable.tree"]);
    expect(documentationOf(model.document, "nope")).toBeUndefined();
  });
});

describe("credentials", () => {
  it("builds Authorization and API key headers, refusing what a browser cannot send", () => {
    expect(credentialHeaders([{ kind: "bearer", token: "Bearer abc.def" }])).toEqual([["authorization", "Bearer abc.def"]]);
    // RFC 7617: UTF-8 user-id ":" password, base64
    expect(credentialHeaders([{ kind: "basic", username: "ü", password: "p" }])).toEqual([["authorization", "Basic w7w6cA=="]]);
    expect(credentialHeaders([{ kind: "api-key", header: "X-API-Key", value: " k1 " }, { kind: "bearer", token: "t" }])).toEqual([["x-api-key", "k1"], ["authorization", "Bearer t"]]);
    expect(credentialProblem({ kind: "header", name: "Cookie", value: "a=b" })).toMatch(/managed by the browser/);
    expect(credentialProblem({ kind: "header", name: "Proxy-Authorization", value: "x" })).toMatch(/do not let pages set/);
    expect(credentialProblem({ kind: "api-key", header: "X Key", value: "x" })).toMatch(/not a valid header name/);
    expect(credentialProblem({ kind: "bearer", token: "tök" })).toMatch(/visible ASCII/);
    expect(credentialProblem({ kind: "basic", username: "a:b", password: "x" })).toMatch(/':'/);
    expect(credentialHeaders([{ kind: "header", name: "Cookie", value: "a=b" }])).toEqual([]);
  });

  it("reads host hints and finds a token to authorize with in a response", () => {
    expect(parseAuthHints('[{"scheme":"Bearer","kind":"bearer","handler":"JwtBearerHandler"},{"scheme":"x","kind":"weird"}]')).toEqual([{ scheme: "Bearer", kind: "bearer", handler: "JwtBearerHandler" }]);
    expect(parseAuthHints("not json")).toEqual([]);
    // Synthetic JWT-shaped text: an empty payload and an invalid one-character signature, never an issued credential.
    expect(tokenField({ tokenType: "Bearer", accessToken: "eyJhbGciOiJIUzI1NiJ9.e30.x", expiresIn: 3600 })).toEqual({ field: "accessToken", token: "eyJhbGciOiJIUzI1NiJ9.e30.x" });
    expect(tokenField({ token: "short" })).toBeUndefined();
    expect(tokenField(["x"])).toBeUndefined();
  });
});

describe("snippets", () => {
  it("names methods and factories as the generator does", () => {
    expect(operationMethodName("users.get")).toBe("usersGet");
    expect(operationMethodName("coverage.nodes-seed")).toBe("coverageNodesSeed");
    expect(operationMethodName("Http.Todos_create")).toBe("httpTodosCreate");
    expect(operationMethodName("class")).toBe("opClass");
    expect(operationMethodName("1st")).toBe("op1st");
    expect(clientFactoryName("sample-api")).toBe("createSampleApiClient");
  });

  it("writes domain values as TypeScript with the runtime helpers they need", () => {
    const helpers = new Set<string>();
    const code = tsLiteral({ id: "x", revision: 9007199254740993n, balance: decimalFromString("42.10"), since: parseDateOnly("2026-10-02", ""), tags: new Map([["a b", 1]]), "a-b": null }, helpers);
    expect(code).toContain("revision: 9007199254740993n");
    expect(code).toContain('balance: decimalFromString("42.10")');
    expect(code).toContain('since: parseDateOnly("2026-10-02")');
    expect(code).toContain('["a b", 1]');
    expect(code).toContain('"a-b": null');
    expect([...helpers].sort()).toEqual(["decimalFromString", "parseDateOnly"]);
  });

  it("writes credentials as placeholders and hides values unless revealed", async () => {
    const model = await load();
    const prepared = prepareRequest(model.registry.operations.get("portable.money")!, { body: { amount: decimalFromString("1.5"), currency: "JPY", note: null } }, { baseUrl: "http://api.test/app" });
    const options = { apiId: "sample-api", operationId: "portable.money", baseUrl: "http://api.test/app", credentials: [{ kind: "bearer", token: "SECRET-TOKEN" }] as const, reveal: false };
    for (const text of [curlSnippet(prepared, options), fetchSnippet(prepared, options), clientSnippet({ ...options, args: { body: { amount: decimalFromString("1.5") } } })]) {
      expect(text).not.toContain("SECRET-TOKEN");
      expect(text).not.toContain("1.5");
    }
    expect(curlSnippet(prepared, options)).toContain('-H "authorization: Bearer $TOKEN"');
    // the exact text the request encoder wrote (PMoney writes its decimal as a string)
    expect(fetchSnippet(prepared, { ...options, reveal: true })).toContain('body: "{\\"amount\\":\\"1.5\\",\\"currency\\":\\"JPY\\",\\"note\\":null}"');
    expect(clientSnippet({ ...options, reveal: true, args: { body: { amount: decimalFromString("1.5") } } })).toContain('await client.portableMoney({\n  body: {\n    amount: decimalFromString("1.5"),');
    expect(clientSnippet({ ...options, reveal: true })).toContain('baseUrl: "http://api.test/app"');
  });
});

describe("highlighting", () => {
  it("colours JSON text without interpreting it, and leaves non-JSON plain", () => {
    const tokens = highlightJson('{"a": [1, true, null, "<b>x</b>"]}');
    expect(tokens.filter((t) => t.kind !== "space" && t.kind !== "punct").map((t) => `${t.kind}:${t.text}`)).toEqual(["key:\"a\"", "number:1", "literal:true", "literal:null", 'string:"<b>x</b>"']);
    expect(tokens.map((t) => t.text).join("")).toBe('{"a": [1, true, null, "<b>x</b>"]}');
    expect(highlightJson("not json").at(-1)).toEqual({ kind: "plain", text: "not json" });
  });
});
