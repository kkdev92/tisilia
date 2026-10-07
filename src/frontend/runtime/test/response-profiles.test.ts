import { expect, it } from "vitest";
import { createCodecContext } from "../src/codec/abi.js";
import { scalarCodec } from "../src/codec/scalars.js";
import { CodecError } from "../src/codec/errors.js";
import { decodeResponse, type OperationDescriptor } from "../src/http/client.js";
import { isFailure } from "../src/http/result.js";

it("decodes with the selected response profile independently of the request profile", () => {
  const scalar = scalarCodec("int64");
  const codec = { ...scalar, decodeResponse: (value: Parameters<NonNullable<typeof scalar.decodeResponse>>[0], context = createCodecContext()) => {
    if (context.profileId !== "response-profile") { throw new CodecError("unsupported", "", "wrong response profile"); }
    return scalar.decodeResponse!(value, context);
  } };
  const op: OperationDescriptor = { id: "profiles", method: "POST", route: "/", profileId: "request-profile", parameters: [], requestExecution: "browser-allowed", requestHeaderAllowlist: [], responses: [{ caseId: "ok", status: 200, hydration: "browser-safe", exposedHeaders: [], body: { kind: "json", mediaType: "application/json", profileId: "response-profile", nullable: false, codec } }] };
  const bytes = new TextEncoder().encode("9007199254740993");
  expect(decodeResponse(op, { kind: "raw", caseId: "ok", status: 200, headers: [], bodyKind: "json", body: bytes, mediaType: "application/json", metadata: { bodyBytes: bytes.length, status: 200, headers: [], mediaType: "application/json" } }, { baseUrl: "http://api.test" })).toMatchObject({ kind: "response", data: 9007199254740993n });
});

it("recognizes completed downloads and subscriptions as successes", () => {
  for (const kind of ["response", "download", "subscription"]) { expect(isFailure({ kind })).toBe(false); }
  for (const kind of ["transport-failure", "codec-failure", "cancelled", "timeout", "limit-failure"]) { expect(isFailure({ kind })).toBe(true); }
});
