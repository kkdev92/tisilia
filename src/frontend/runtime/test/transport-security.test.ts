import { describe, expect, it } from "vitest";
import { send } from "../src/http/transport.js";

describe("transport failure disclosure", () => {
  const secret = "synthetic-private-header-value";
  const request = { url: new URL("https://api.example.test/items"), method: "GET", headers: [["authorization", `Bearer ${secret}`]] as const };

  it.each(["message", "cause", "string", "read"] as const)("keeps %s error contents out of public diagnostics", async stage => {
    const fetch: typeof globalThis.fetch = async () => {
      if (stage === "read") {
        return new Response(new ReadableStream({ start(controller) { controller.error(new Error(secret)); } }));
      }
      if (stage === "string") { throw secret; }
      if (stage === "cause") { throw new TypeError("fetch failed", { cause: new Error(`adapter included ${secret}`) }); }
      throw new Error(`Authorization: Bearer ${secret}`);
    };
    const result = await send(request, { fetch });
    expect(result).toMatchObject({ kind: "transport-failure", reason: stage === "read" ? "read" : "network" });
    expect(JSON.stringify(result)).not.toContain(secret);
  });

  it("handles a thrown value whose string conversion throws", async () => {
    const result = await send(request, { fetch: async () => { throw { toString() { throw new Error(secret); } }; } });
    expect(result).toMatchObject({ kind: "transport-failure", reason: "network" });
    expect(JSON.stringify(result)).not.toContain(secret);
  });

  it.each(["redirect", "cors"] as const)("preserves the %s classification without disclosing the cause", async reason => {
    const result = await send(request, { fetch: async () => { throw new TypeError("fetch failed", { cause: new Error(`${reason}: ${secret}`) }); } });
    expect(result).toMatchObject({ kind: "transport-failure", reason });
    expect(JSON.stringify(result)).not.toContain(secret);
  });
});
