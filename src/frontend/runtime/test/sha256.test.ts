import { createHash, randomBytes } from "node:crypto";
import { afterEach, describe, expect, it, vi } from "vitest";
import { sha256 } from "../src/sha256.js";
import { computeRequestIdentity, createRequestIdentityRecord, sha256Hex } from "../src/identity.js";

const hex = (bytes: Uint8Array): string => Buffer.from(bytes).toString("hex");
const ascii = (text: string): Uint8Array => new TextEncoder().encode(text);

describe("sha256 without Web Crypto", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  // the byte-oriented SHA-256 cases of RFC 6234 §8.5 (tests 1–4, 6, 8, 10)
  it.each([
    ["abc", ascii("abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"],
    ["448 bits", ascii("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"), "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"],
    ["a million a", ascii("a".repeat(1_000_000)), "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"],
    ["ten 512-bit blocks", ascii("01234567".repeat(80)), "594847328451bdfa85056225462cc1d867d877fb388df0ce35f25ab5562bfbb5"],
    ["0x19", Uint8Array.of(0x19), "68aa2e2ee5dff96e3355e6c7ee373e3d6a4e17f75f9518d843709c0c9bc3e3d4"],
    ["TEST8_256", Buffer.from("e3d72570dcdd787ce3887ab2cd684652", "hex"), "175ee69b02ba9b58e2b0a5fd13819cea573f3940a94f825128cf4209beabb4e8"],
    [
      "TEST10_256",
      Buffer.from(
        "8326754e2277372f4fc12b20527afef04d8a056971b11ad57123a7c137760000d7bef6f3c1f7a9083aa39d810db310777dab8b1e7f02b84a26c773325f8b2374de7a4b5a58cb5c5cf35bcee6fb946e5bd694fa593a8beb3f9d6592ecedaa66ca82a29d0c51bcf9336230e5d784e4c0a43f8d79a30a165cbabe452b774b9c7109a97d138f129228966f6c0adc106aad5a9fdd30825769b2c671af6759df28eb393d54d6",
        "hex",
      ),
      "97dbca7df46d62c8a422c941dd7e835b8ad3361763f7e9b2d95f4f0da6e1ccbc",
    ],
  ])("matches RFC 6234: %s", (_name, input, expected) => {
    expect(hex(sha256(input))).toBe(expected);
  });

  it("agrees with node:crypto on every padding boundary", () => {
    // 55/56 bytes decide whether the length fits in the last block; 64 multiples start a fresh one
    for (let length = 0; length <= 300; length++) {
      const input = randomBytes(length);
      expect(hex(sha256(input)), `length ${length}`).toBe(createHash("sha256").update(input).digest("hex"));
    }
  });

  it("hashes a view of a larger buffer, not the buffer", () => {
    const backing = randomBytes(200);
    const view = new Uint8Array(backing.buffer, backing.byteOffset + 17, 100);
    expect(hex(sha256(view))).toBe(createHash("sha256").update(view).digest("hex"));
  });

  it("gives the request identity of a page without crypto.subtle (plain http on a LAN address)", async () => {
    const record = createRequestIdentityRecord({
      operationId: "users.get",
      method: "GET",
      encodedPath: "/users/1",
      queryEntries: [{ name: "q", value: "東京" }],
      selectedHeaderEntries: [],
      bodyKind: "none",
      bodyText: "",
      semanticHash: "sha256:" + "a".repeat(64),
      scopeNonce: "0123456789abcdef0123456789abcdef",
    });
    const secure = await computeRequestIdentity(record);
    // an insecure context keeps getRandomValues but has no subtle (W3C Web Cryptography: [SecureContext] subtle)
    vi.stubGlobal("crypto", { getRandomValues: crypto.getRandomValues.bind(crypto), subtle: undefined });
    expect(globalThis.crypto.subtle).toBeUndefined();
    expect(await computeRequestIdentity(record)).toBe(secure);
    expect(await sha256Hex(ascii("abc"))).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  });
});
