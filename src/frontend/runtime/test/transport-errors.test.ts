import { describe, expect, it } from "vitest";
import { send } from "../src/http/transport.js";

// How a rejected fetch is classified. Node's fetch (undici) names the reason in `cause`; browsers reject with a TypeError whose
// message never says why (Fetch turns a refused redirect into a plain network error, CORS details only reach the console).
const request = { url: new URL("http://api.test/users/1"), method: "GET", headers: [] } as const;
const rejecting = (error: unknown) => (async () => { throw error; }) as unknown as typeof fetch;

describe("transport failures", () => {
  it.each([
    ["Chrome", "Failed to fetch"],
    ["Firefox", "NetworkError when attempting to fetch resource."],
    ["Safari", "Load failed"],
  ])("says what a %s fetch error may have been", async (_browser, message) => {
    const outcome = await send(request, { fetch: rejecting(new TypeError(message)) });
    expect(outcome).toMatchObject({ kind: "transport-failure", reason: "network" });
    if (outcome.kind === "transport-failure") {
      expect(outcome.message).not.toContain(message);
      expect(outcome.message).toContain("CORS");
      expect(outcome.message).toContain("redirect");
    }
  });

  it("classifies the causes Node's fetch gives without copying their contents", async () => {
    const redirect = await send(request, { fetch: rejecting(new TypeError("fetch failed", { cause: new Error("unexpected redirect") })) });
    expect(redirect).toMatchObject({ kind: "transport-failure", reason: "redirect" });
    expect(JSON.stringify(redirect)).not.toContain("unexpected redirect");
    const refused = await send(request, { fetch: rejecting(new TypeError("fetch failed", { cause: new Error("connect ECONNREFUSED 127.0.0.1:1") })) });
    expect(refused).toMatchObject({ kind: "transport-failure", reason: "network" });
    expect(JSON.stringify(refused)).not.toContain("127.0.0.1:1");
  });

  it("does not guess for errors of a custom fetch", async () => {
    const outcome = await send(request, { fetch: rejecting(new Error("adapter is closed")) });
    expect(outcome).toMatchObject({ kind: "transport-failure", reason: "network" });
    expect(JSON.stringify(outcome)).not.toContain("adapter is closed");
    const thrown = await send(request, { fetch: rejecting("plain string") });
    expect(thrown).toMatchObject({ kind: "transport-failure", reason: "network" });
    expect(JSON.stringify(thrown)).not.toContain("plain string");
  });

  it("keeps URLs and query strings out of platform error diagnostics", async () => {
    const outcome = await send(request, { fetch: rejecting(new TypeError("Failed to fetch http://api.test/users?token=secret")) });
    expect(outcome).toMatchObject({ kind: "transport-failure", reason: "network" });
    expect(JSON.stringify(outcome)).not.toContain("api.test");
    expect(JSON.stringify(outcome)).not.toContain("secret");
  });
});
