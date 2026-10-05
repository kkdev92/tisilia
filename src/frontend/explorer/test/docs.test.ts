import { describe, expect, it } from "vitest";
import type { ContractDocument } from "@kkdev92/tisilia-runtime";
import { docOf, isDeprecated, splitDescription } from "../src/docs.js";
import { parseInline, parseMarkdown } from "../src/markdown.js";

describe("documentation text", () => {
  it("reads the Markdown subset the exporter writes: paragraphs, breaks, lists, code, emphasis, links", () => {
    expect(parseMarkdown("First line\nsecond line.\n\n- one\n- two\ncontinued\n\n1. a\n2) b\n\n```\nvar x = 1;\n  y();\n```")).toEqual([
      { kind: "paragraph", children: [{ kind: "text", text: "First line" }, { kind: "break" }, { kind: "text", text: "second line." }] },
      { kind: "list", ordered: false, items: [[{ kind: "text", text: "one" }], [{ kind: "text", text: "two continued" }]] },
      { kind: "list", ordered: true, items: [[{ kind: "text", text: "a" }], [{ kind: "text", text: "b" }]] },
      { kind: "code", text: "var x = 1;\n  y();" },
    ]);
    expect(parseInline("**Deprecated.** Use `code`, *not* this; see [the guide](https://example.com/a_b).")).toEqual([
      { kind: "strong", children: [{ kind: "text", text: "Deprecated." }] },
      { kind: "text", text: " Use " },
      { kind: "code", text: "code" },
      { kind: "text", text: ", " },
      { kind: "em", children: [{ kind: "text", text: "not" }] },
      { kind: "text", text: " this; see " },
      { kind: "link", href: "https://example.com/a_b", children: [{ kind: "text", text: "the guide" }] },
      { kind: "text", text: "." },
    ]);
    // a backtick inside code, snake_case and arithmetic stay what they are
    expect(parseInline("`` a`b `` snake_case_name 2 * 3")).toEqual([{ kind: "code", text: "a`b" }, { kind: "text", text: " snake_case_name 2 * 3" }]);
  });

  it("keeps HTML and other link schemes as literal text", () => {
    expect(parseInline('<img src=x onerror="alert(1)"> [x](javascript:alert(1)) [y](data:text/html,1)')).toEqual([
      { kind: "text", text: '<img src=x onerror="alert(1)"> [x](javascript:alert(1)) [y](data:text/html,1)' },
    ]);
    expect(parseMarkdown("<script>alert(1)</script>")).toEqual([{ kind: "paragraph", children: [{ kind: "text", text: "<script>alert(1)</script>" }] }]);
  });

  it("splits a type's description into its text and the member list", () => {
    const { text, members } = splitDescription("Kept for a year.\n\n**Members**\n\n- `id` — The id.\n- `display-name` — Shown — with a dash.\n");
    expect(text).toBe("Kept for a year.");
    expect([...members]).toEqual([
      ["id", "The id."],
      ["display-name", "Shown — with a dash."],
    ]);
    expect(splitDescription("Only text.")).toEqual({ text: "Only text.", members: new Map() });
    // the heading used for text of its own: nothing after it is lost
    const own = "Intro.\n\n**Members**\n\nAre listed elsewhere.\n- `id` — The id.";
    expect(splitDescription(own)).toEqual({ text: own, members: new Map() });
    expect(splitDescription("**Members**")).toEqual({ text: "**Members**", members: new Map() });
  });

  it("tells a deprecated target by the mark its text starts with ([Obsolete])", () => {
    expect(isDeprecated("**Deprecated.** Use PATCH.")).toBe(true);
    expect(isDeprecated("\n**Deprecated.**")).toBe(true);
    expect(isDeprecated("Not **Deprecated.** at the start")).toBe(false);
    expect(isDeprecated("")).toBe(false);
    expect(isDeprecated(undefined)).toBe(false);
  });

  it("finds an entry by id, split for display", () => {
    const document = {
      documentation: [
        { targetId: "users.get", summary: "Get a user", description: "" },
        { targetId: "api.User", summary: "A user.", description: "**Members**\n\n- `name` — The name." },
        { targetId: "empty", summary: "", description: "" },
      ],
    } as unknown as ContractDocument;
    expect(docOf(document, "users.get")).toEqual({ summary: "Get a user", text: "", members: new Map() });
    expect(docOf(document, "api.User")?.members.get("name")).toBe("The name.");
    expect(docOf(document, "empty")).toBeUndefined();
    expect(docOf(document, "missing")).toBeUndefined();
    expect(docOf(document, undefined)).toBeUndefined();
  });
});
