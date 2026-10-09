import { describe, expect, it, vi } from "vitest";
import { execute, prepareRequest, type OperationDescriptor } from "../src/http/client.js";
import type { FormFieldDescriptor } from "../src/http/forms.js";
import { decimalFromString } from "../src/primitives/decimal.js";
import { parseDateOnly, parseDateTime, parseDuration } from "../src/primitives/datetime.js";

const operation: OperationDescriptor = {
  id: "form", method: "POST", route: "/form", parameters: [], requestExecution: "browser-allowed", requestHeaderAllowlist: [],
  requestBody: { kind: "form", mediaType: "multipart/form-data", presence: "required", get: a => (a as { body: unknown }).body, fields: [
    { name: "id", kind: "value", scalar: "int64", repeated: false, presence: "required" },
    { name: "tags", kind: "value", scalar: "string", repeated: true, presence: "optional" },
    { name: "files", kind: "file", repeated: true, presence: "optional" },
  ] },
  responses: [{ caseId: "ok", status: 204, body: { kind: "none" }, hydration: "browser-safe", exposedHeaders: [] }],
};
const base = { baseUrl: "http://api.test" };

describe("encoded forms", () => {
  const grouped: OperationDescriptor = { ...operation, requestBody: { kind: "form", mediaType: "multipart/form-data", presence: "required", get: a => a, fields: [
    { name: "Items", kind: "object", presence: "required", repeated: true, indexed: true, fields: [
      { name: "Id", kind: "value", scalar: "int64", repeated: false, presence: "optional" },
      { name: "Tags", kind: "value", scalar: "string", repeated: true, indexed: true, presence: "optional" },
      { name: "File", kind: "file", repeated: false, presence: "optional" },
      { name: "Children", kind: "object", repeated: true, indexed: true, presence: "optional", fields: [
        { name: "Name", kind: "value", scalar: "string", repeated: false, presence: "required" },
      ] },
    ] },
  ] } };
  it("encodes complex collections, nested collections and files with one shared byte budget", async () => {
    const args = { Items: [{ Id: 9007199254740993n, Tags: ["日本", "two"], File: { bytes: new Uint8Array([0, 255]), fileName: "x.bin" }, Children: [{ Name: "first" }, { Name: "second" }] }, { Id: -9007199254740993n }] };
    const prepared = prepareRequest(grouped, args, base);
    const form = await new Response(prepared.bodyBytes as BodyInit, { headers: prepared.headers as [string, string][] }).formData();
    expect(form.get("Items[0].Id")).toBe("9007199254740993");
    expect(form.get("Items[0].Tags[1]")).toBe("two");
    expect(form.get("Items[0].Children[1].Name")).toBe("second");
    expect(form.get("Items[1].Id")).toBe("-9007199254740993");
    expect([...new Uint8Array(await (form.get("Items[0].File") as File).arrayBuffer())]).toEqual([0, 255]);
    const size = prepared.bodyBytes!.length;
    expect(prepareRequest(grouped, args, { ...base, limits: { maxBodyBytes: size } }).bodyBytes).toHaveLength(size);
    expect(() => prepareRequest(grouped, args, { ...base, limits: { maxBodyBytes: size - 1 } })).toThrow("maxBodyBytes");
  });
  it.each([[], [{}], [{ Id: 1n }, {}, { Id: 2n }], Array(2), [{ Id: 1n, unknown: true }], [{ Children: [] }], [null]].map(Items => ({ Items })))("rejects disappearing or undeclared complex items before credentials: %#", async ({ Items }) => {
    const credentialProvider = vi.fn(); const fetch = vi.fn();
    const result = await execute(grouped, { Items }, { ...base, credentialProvider, transport: { fetch } });
    expect(result.kind).toBe("transport-failure");
    expect(credentialProvider).not.toHaveBeenCalled(); expect(fetch).not.toHaveBeenCalled();
  });
  it("rejects overlapping wire names in directly supplied descriptors", () => {
    const op: OperationDescriptor = { ...grouped, requestBody: { kind: "form", mediaType: "application/x-www-form-urlencoded", presence: "required", get: a => a, fields: [
      ...(grouped.requestBody!.kind === "form" ? grouped.requestBody!.fields : []),
      { name: "items[0].id", kind: "value", scalar: "int64", repeated: false, presence: "optional" },
    ] } };
    expect(() => prepareRequest(op, { Items: [{ Id: 1n }], "items[0].id": 2n }, base)).toThrow("overlapping");
  });
  it("writes root scalar and complex collections without the argument name prefix", () => {
    const op: OperationDescriptor = { ...grouped, requestBody: { kind: "form", mediaType: "application/x-www-form-urlencoded", presence: "required", get: a => a, fields: [
      { name: "values", kind: "value", scalar: "int64", repeated: true, indexed: true, wireName: "", presence: "required" },
    ] } };
    expect([...new URLSearchParams(prepareRequest(op, { values: [1n, 9007199254740993n] }, base).bodyText)]).toEqual([["[0]", "1"], ["[1]", "9007199254740993"]]);
    const complex: OperationDescriptor = { ...grouped, requestBody: { ...grouped.requestBody!, kind: "form", mediaType: "application/x-www-form-urlencoded", fields: grouped.requestBody!.kind === "form" ? grouped.requestBody!.fields.map(f => ({ ...f, wireName: "" as const })) : [] } };
    expect([...new URLSearchParams(prepareRequest(complex, { Items: [{ Id: 1n }, { Id: 2n }] }, base).bodyText)]).toEqual([["[0].Id", "1"], ["[1].Id", "2"]]);
  });
  it("encodes indexed fields and refuses an empty collection that would disappear on the wire", async () => {
    const op: OperationDescriptor = { ...operation, requestBody: { kind: "form", mediaType: "application/x-www-form-urlencoded", presence: "required", get: a => a, fields: [{ name: "Details.Tags", kind: "value", scalar: "string", presence: "optional", repeated: true, indexed: true }] } };
    const prepared = prepareRequest(op, { "Details.Tags": ["日本", "two"] }, base);
    expect([...new URLSearchParams(prepared.bodyText)]).toEqual([["Details.Tags[0]", "日本"], ["Details.Tags[1]", "two"]]);
    expect(() => prepareRequest(op, { "Details.Tags": [] }, base)).toThrow("at least one");
    expect(prepareRequest(op, {}, base).bodyText).toBe("");
  });
  it("matches .NET blank-to-null semantics, including NEL and excluding BOM", () => {
    const op: OperationDescriptor = { ...operation, requestBody: { kind: "form", mediaType: "application/x-www-form-urlencoded", presence: "required", get: a => a, fields: [{ name: "value", kind: "value", scalar: "string", presence: "required", repeated: false, rejectBlank: true }] } };
    for (const value of ["", "\u0085", "\t\n ", "\u3000"]) { expect(() => prepareRequest(op, { value }, base)).toThrow("blank"); }
    expect(prepareRequest(op, { value: "\ufeff" }, base).bodyText).toBe("value=%EF%BB%BF");
  });
  it("prepares deterministic multipart bytes that native FormData reads without precision loss", async () => {
    const args = { body: { id: 9007199254740993n, tags: ["日本😀\r\n+&=", ""], files: [{ bytes: new Uint8Array([99, 0, 255, 195, 40, 88]).subarray(1, 5), fileName: "日本.bin" }, { bytes: new Uint8Array(), fileName: "empty.bin" }] } };
    const a = prepareRequest(operation, args, base), b = prepareRequest(operation, args, base);
    expect(a).toEqual(b); expect(a.bodyText).toBeUndefined();
    const parsed = await new Response(a.bodyBytes as BodyInit, { headers: a.headers as [string, string][] }).formData();
    expect(parsed.get("id")).toBe("9007199254740993"); expect(parsed.getAll("tags")).toEqual(["日本😀\r\n+&=", ""]);
    const files = parsed.getAll("files") as File[];
    expect(files.map(f => f.name)).toEqual(["日本.bin", "empty.bin"]);
    expect([...new Uint8Array(await files[0]!.arrayBuffer())]).toEqual([0, 255, 195, 40]); expect(files[1]!.size).toBe(0);
    args.body.files[0]!.bytes.fill(7);
    expect(a.bodyBytes).toEqual(b.bodyBytes);
  });

  it("writes urlencoded fields with exact Unicode, reserved characters and repetitions", () => {
    const op = { ...operation, requestBody: { ...operation.requestBody!, kind: "form" as const, mediaType: "application/x-www-form-urlencoded", fields: operation.requestBody!.kind === "form" ? operation.requestBody!.fields.slice(0, 2) : [] } };
    const prepared = prepareRequest(op, { body: { id: 9007199254740993n, tags: ["a +&=", "日本😀", ""] } }, base);
    expect([...new URLSearchParams(prepared.bodyText)]).toEqual([["id", "9007199254740993"], ["tags", "a +&="], ["tags", "日本😀"], ["tags", ""]]);
  });

  it("writes values the server reads with the request culture without decimal separators or bare dates", () => {
    const fields = ([
      ["dec", "decimal"], ["decWhole", "decimal"], ["decZero", "decimal"], ["dbl", "float64"], ["dblBig", "float64"], ["dblTiny", "float64"], ["dblMax", "float64"],
      ["f32", "float32"], ["negative", "int32"], ["day", "date-only"], ["when", "datetime"], ["span", "duration"],
    ] as const).map(([name, scalar]) => ({ name, kind: "value" as const, scalar, repeated: false, requestCulture: true, presence: "required" as const }));
    const form = (requestCulture: boolean): OperationDescriptor => ({ ...operation, requestBody: { kind: "form", mediaType: "application/x-www-form-urlencoded", presence: "required", get: a => a,
      fields: fields.map(f => ({ ...f, requestCulture })) } });
    const args = {
      dec: decimalFromString("-1.50"), decWhole: decimalFromString("100"), decZero: decimalFromString("0.00"), dbl: 1.5, dblBig: 1e21, dblTiny: 1.5e-7, dblMax: Number.MAX_VALUE,
      f32: Math.fround(0.1), negative: -3, day: parseDateOnly("2026-10-08"), when: parseDateTime("2026-10-08T12:34:56.1234567Z"), span: parseDuration("-1.02:03:04.5000000"),
    };
    expect(Object.fromEntries(new URLSearchParams(prepareRequest(form(true), args, base).bodyText))).toEqual({
      dec: "-150E-2", decWhole: "100", decZero: "0E-2", dbl: "15E-1", dblBig: "1" + "0".repeat(21), dblTiny: "15E-8", dblMax: "17976931348623157" + "0".repeat(292),
      f32: "1E-1", negative: "-3", day: "2026-10-08T00:00:00", when: "2026-10-08T12:34:56.1234567Z", span: "-1.02:03:04.5000000",
    });
    // without requestCulture the same values keep the invariant canonical text
    const invariant = new URLSearchParams(prepareRequest(form(false), args, base).bodyText);
    expect([invariant.get("dec"), invariant.get("dbl"), invariant.get("day")]).toEqual(["-1.50", "1.5", "2026-10-08"]);
  });

  it.each(["application/x-www-form-urlencoded", "multipart/form-data"])("writes dictionary entries as name[key] and a root dictionary as [key]: %s", async mediaType => {
    const op = (fields: FormFieldDescriptor[]): OperationDescriptor => ({ ...operation, requestBody: { kind: "form", mediaType, presence: "required", get: a => a, fields } });
    const model = op([
      { name: "Labels", kind: "map", key: "string", scalar: "string", repeated: false, presence: "optional" },
      { name: "Counts", kind: "map", key: "int64", scalar: "int64", repeated: false, presence: "optional" },
    ]);
    const read = async (prepared: ReturnType<typeof prepareRequest>) => [...await new Response(prepared.bodyBytes as BodyInit, { headers: prepared.headers as [string, string][] }).formData()];
    expect(await read(prepareRequest(model, { Labels: new Map([["a.b", "1"], ["x[y", "2"], ["", "empty"]]), Counts: new Map([[-9007199254740993n, 9007199254740993n]]) }, base)))
      .toEqual([["Labels[a.b]", "1"], ["Labels[x[y]", "2"], ["Labels[]", "empty"], ["Counts[-9007199254740993]", "9007199254740993"]]);
    const root = op([{ name: "values", kind: "map", key: "string", scalar: "string", repeated: false, wireName: "", presence: "required" }]);
    expect(await read(prepareRequest(root, { values: new Map([["k", "v"]]) }, base))).toEqual([["[k]", "v"]]);
    // the server reads a key up to its first ']' and merges keys that differ only in case; an empty map would send nothing
    expect(() => prepareRequest(root, { values: new Map([["a]b", "v"]]) }, base)).toThrow("cannot contain ']'");
    expect(() => prepareRequest(root, { values: new Map([["k", "1"], ["K", "2"]]) }, base)).toThrow("differ ignoring case");
    expect(() => prepareRequest(root, { values: new Map() }, base)).toThrow("at least one entry");
    expect(() => prepareRequest(root, { values: { k: "v" } }, base)).toThrow("requires a Map");
    // a key never meets another field's name
    const overlapping = op([
      { name: "Labels", kind: "map", key: "string", scalar: "string", repeated: false, presence: "optional" },
      { name: "Labels[x]", kind: "value", scalar: "string", repeated: false, presence: "optional" },
    ]);
    expect(() => prepareRequest(overlapping, { Labels: new Map([["X", "1"]]), "Labels[x]": "2" }, base)).toThrow("overlapping");
  });

  it.each(["application/x-www-form-urlencoded", "multipart/form-data"])("writes member names with path or index delimiters as exactly those keys: %s", async mediaType => {
    const op: OperationDescriptor = { ...operation, requestBody: { kind: "form", mediaType, presence: "required", get: a => a, fields: [
      { name: "a.b", kind: "value", scalar: "string", repeated: false, presence: "optional" },
      { name: "x[y]", kind: "value", scalar: "string", repeated: false, presence: "optional" },
      { name: "p]q", kind: "value", scalar: "string", repeated: false, presence: "optional" },
      { name: "q[", kind: "value", scalar: "string", repeated: false, presence: "optional" },
      { name: "Items", kind: "object", repeated: true, indexed: true, presence: "optional", fields: [
        { name: "a.b", kind: "value", scalar: "string", repeated: false, presence: "required" },
      ] },
    ] } };
    const prepared = prepareRequest(op, { "a.b": "1", "x[y]": "2", "p]q": "3", "q[": "4", Items: [{ "a.b": "5" }, { "a.b": "6" }] }, base);
    const form = await new Response(prepared.bodyBytes as BodyInit, { headers: prepared.headers as [string, string][] }).formData();
    expect([...form]).toEqual([["a.b", "1"], ["x[y]", "2"], ["p]q", "3"], ["q[", "4"], ["Items[0].a.b", "5"], ["Items[1].a.b", "6"]]);
  });

  it.each(["bad\r\nX: yes", 'bad"quote', "path/file", "bad\\name", "\ud800"])("rejects unsafe filenames before fetching: %j", async name => {
    const fetch = vi.fn();
    expect((await execute(operation, { body: { id: 1n, files: [{ bytes: new Uint8Array(), fileName: name }] } }, { ...base, transport: { fetch } })).kind).toBe("transport-failure");
    expect(fetch).not.toHaveBeenCalled();
  });

  it("bounds the complete encoded body and rejects missing/extra fields before credentials", async () => {
    const credentials = vi.fn(); const fetch = vi.fn();
    const args = { body: { id: 1n } }; const length = prepareRequest(operation, args, base).bodyBytes!.length;
    expect((await execute(operation, args, { ...base, credentialProvider: credentials, transport: { fetch }, limits: { maxBodyBytes: length - 1 } })).kind).toBe("limit-failure");
    expect(prepareRequest(operation, args, { ...base, limits: { maxBodyBytes: length } }).bodyBytes).toHaveLength(length);
    expect(() => prepareRequest(operation, { body: {} }, base)).toThrow("required");
    expect(() => prepareRequest(operation, { body: { id: 1n, extra: "value" } }, base)).toThrow("undeclared");
    expect(credentials).not.toHaveBeenCalled(); expect(fetch).not.toHaveBeenCalled();
  });
});
