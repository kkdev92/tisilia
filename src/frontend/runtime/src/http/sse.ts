import { JsonParseError } from "../json/parser.js";

/** An SSE event. IDs and retry delays persist according to the event-stream protocol. */
export interface ServerSentEvent<T> {
  readonly data: T;
  readonly event: string;
  readonly id: string;
  /** Decimal milliseconds as text, so even a large server value is preserved exactly. No automatic retry is performed. */
  readonly retryMilliseconds?: string;
}

/** Incremental line parser; completed events are yielded before later input is inspected. */
export class SseParser {
  private readonly decoder = new TextDecoder("utf-8", { fatal: true });
  private parts: string[] = [];
  private skipLf = false;
  private data: string[] = [];
  private event = "";
  private id = "";
  private retry: string | undefined;

  *push(bytes: Uint8Array): Generator<ServerSentEvent<string>> {
    let text: string;
    try { text = this.decoder.decode(bytes, { stream: true }); }
    catch { throw new JsonParseError("invalid-utf8", 0, "event stream is not valid UTF-8"); }
    let start = 0;
    for (let i = 0; i < text.length; i++) {
      const c = text.charCodeAt(i);
      if (this.skipLf) {
        this.skipLf = false;
        if (c === 10) { start = i + 1; continue; }
      }
      if (c !== 10 && c !== 13) { continue; }
      this.parts.push(text.slice(start, i));
      const line = this.parts.join("");
      this.parts = [];
      this.skipLf = c === 13;
      start = i + 1;
      if (line === "") {
        const event = this.data.length === 0 ? undefined : { data: this.data.join("\n"), event: this.event || "message", id: this.id, ...(this.retry === undefined ? {} : { retryMilliseconds: this.retry }) };
        this.data = [];
        this.event = "";
        if (event !== undefined) { yield event; }
      } else {
        const colon = line.indexOf(":");
        const name = colon < 0 ? line : line.slice(0, colon);
        let value = colon < 0 ? "" : line.slice(colon + 1);
        if (value.startsWith(" ")) { value = value.slice(1); }
        switch (name) {
          case "data": this.data.push(value); break;
          case "event": this.event = value; break;
          case "id": if (!value.includes("\0")) { this.id = value; } break;
          case "retry": if (/^[0-9]+$/.test(value)) { this.retry = value; } break;
        }
      }
    }
    if (start < text.length) { this.parts.push(text.slice(start)); }
  }

  finish(): void {
    // EOF never dispatches an unfinished event. A truncated UTF-8 sequence is still an encoding failure.
    try { this.decoder.decode(); }
    catch { throw new JsonParseError("invalid-utf8", 0, "event stream is not valid UTF-8"); }
    this.parts = [];
    this.data = [];
  }
}
