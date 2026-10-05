import { safeFileName, type BufferedFile, type ContractOperation } from "@kkdev92/tisilia-runtime";

export function downloadPolicy(operation: ContractOperation, reveal: boolean): { allowed: boolean; filenameAllowed: boolean } {
  const rules = operation.security.redaction?.rules.filter(r => r.direction === "response" && r.action !== "show") ?? [];
  return {
    allowed: reveal && !rules.some(r => r.selector.kind === "body-path"),
    filenameAllowed: reveal && !rules.some(r => r.selector.kind === "header" && r.selector.name?.toLowerCase() === "content-disposition"),
  };
}

/** A failed transfer has no selected, decoded file. Its diagnostic headers must not disclose a download filename. */
export function downloadDiagnosticHeaders(operation: ContractOperation, headers: readonly (readonly [string, string])[]): readonly (readonly [string, string])[] {
  return operation.responses.some(r => r.body.kind === "binary")
    ? headers.filter(([name]) => name.toLowerCase() !== "content-disposition")
    : headers;
}

/** One URL per retained result. Its lifetime ends on replacement, hiding, unmount or sign-out, never before the browser consumes a click. */
export class DownloadLease {
  private url: string | undefined;
  private bytes: Uint8Array | undefined;

  save(file: BufferedFile, useName: boolean): void {
    if (this.bytes !== file.bytes || this.url === undefined) {
      this.dispose();
      this.url = URL.createObjectURL(new Blob([file.bytes as BlobPart], { type: "application/octet-stream" }));
      this.bytes = file.bytes;
    }
    const anchor = document.createElement("a");
    anchor.href = this.url;
    anchor.download = useName ? safeFileName(file.suggestedFileName ?? "") ?? "download.bin" : "download.bin";
    document.body.append(anchor);
    try { anchor.click(); } finally { anchor.remove(); }
  }

  dispose(): void {
    if (this.url !== undefined) { URL.revokeObjectURL(this.url); }
    this.url = undefined;
    this.bytes = undefined;
  }
}
