import { describe, expect, it } from "vitest";
import type { ContractOperation } from "@kkdev92/tisilia-runtime";
import { downloadDiagnosticHeaders, downloadPolicy } from "../src/download.js";

describe("binary download disclosure", () => {
  const operation = { security: { redaction: { default: "mask", rules: [] } } } as unknown as ContractOperation;
  it("BD41 requires reveal, keeps explicit body masks and never uses a masked filename", () => {
    expect(downloadPolicy(operation, false)).toEqual({ allowed: false, filenameAllowed: false });
    expect(downloadPolicy(operation, true)).toEqual({ allowed: true, filenameAllowed: true });
    const header: ContractOperation = { ...operation, security: { ...operation.security, redaction: { default: "mask", rules: [{ direction: "response", action: "mask", selector: { kind: "header", name: "Content-Disposition" } }] } } };
    expect(downloadPolicy(header, true)).toEqual({ allowed: true, filenameAllowed: false });
    const body: ContractOperation = { ...operation, security: { ...operation.security, redaction: { default: "mask", rules: [{ direction: "response", action: "omit", selector: { kind: "body-path", segments: [] } }] } } };
    expect(downloadPolicy(body, true).allowed).toBe(false);
  });
  it("BD40 withholds a filename from failed file-transfer diagnostic metadata", () => {
    const file = { ...operation, responses: [{ body: { kind: "binary", mediaType: "application/pdf" }, exposedHeaders: [] }] } as unknown as ContractOperation;
    const metadata = [["content-type", "application/pdf"], ["Content-Disposition", "attachment; filename=secret.pdf"]] as const;
    expect(downloadDiagnosticHeaders(file, metadata)).toEqual([["content-type", "application/pdf"]]);
    expect(JSON.stringify(downloadDiagnosticHeaders(file, metadata))).not.toContain("secret.pdf");
    const json = { ...operation, responses: [{ body: { kind: "json" } }] } as unknown as ContractOperation;
    expect(downloadDiagnosticHeaders(json, metadata)).toBe(metadata);
  });
});
