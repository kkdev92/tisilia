import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { createContractRegistry, type ContractDocument } from "../src/contract/interpreter.js";
import { parseHydrationEnvelope } from "../src/envelope.js";

const contract = JSON.parse(readFileSync(new URL("../../../../tests/fixtures/sample-api.contract.json", import.meta.url), "utf8")) as ContractDocument;

describe("initial specification version", () => {
  it("loads the exported 0.1 contract", () => {
    expect(contract.version).toBe("0.1");
    expect(() => createContractRegistry(contract)).not.toThrow();
  });

  it.each(["0.3", "0.4"])("rejects a development contract with version %s", (version) => {
    const incompatible = { ...contract, version } as unknown as ContractDocument;
    expect(() => createContractRegistry(incompatible)).toThrow("expected tisilia.contract 0.1");
  });

  it.each(["0.3", "0.4"])("rejects a hydration envelope with version %s", (version) => {
    const envelope = {
      format: "tisilia.hydration-envelope", version, kind: "bodyless", status: 204, headers: [],
      semanticHash: "sha256:" + "a".repeat(64), operationId: "users.delete", responseCaseId: "users.delete.ok",
      requestIdentity: "rid:sha256:" + "b".repeat(64), scopeNonce: "0123456789abcdef",
    };
    expect(() => parseHydrationEnvelope(envelope)).toThrow("not a tisilia.hydration-envelope 0.1");
  });
});
