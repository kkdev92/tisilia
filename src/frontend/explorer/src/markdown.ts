// The Markdown the contract's documentation is written in, as a small safe subset (text is shown as text, never as
// HTML): paragraphs (a single line break stays a break), bullet and numbered lists, fenced code, inline code, **strong**, *emphasis*
// and links to http(s) addresses. Anything else — HTML included — stays literal text.

export type Inline =
  | { readonly kind: "text"; readonly text: string }
  | { readonly kind: "code"; readonly text: string }
  | { readonly kind: "strong"; readonly children: readonly Inline[] }
  | { readonly kind: "em"; readonly children: readonly Inline[] }
  | { readonly kind: "link"; readonly href: string; readonly children: readonly Inline[] }
  | { readonly kind: "break" };

export type Block =
  | { readonly kind: "paragraph"; readonly children: readonly Inline[] }
  | { readonly kind: "code"; readonly text: string }
  | { readonly kind: "list"; readonly ordered: boolean; readonly items: readonly (readonly Inline[])[] };

const fence = /^\s*```/;
const bullet = /^\s*[-*]\s+(.*)$/;
const numbered = /^\s*\d+[.)]\s+(.*)$/;

export function parseMarkdown(text: string): Block[] {
  const lines = text.replace(/\r\n?/g, "\n").split("\n");
  const blocks: Block[] = [];
  let paragraph: string[] = [];
  let list: { ordered: boolean; items: string[] } | undefined;
  const endParagraph = (): void => {
    if (paragraph.length > 0) {
      blocks.push({ kind: "paragraph", children: parseInline(paragraph.join("\n")) });
      paragraph = [];
    }
  };
  const endList = (): void => {
    if (list !== undefined) {
      blocks.push({ kind: "list", ordered: list.ordered, items: list.items.map((item) => parseInline(item)) });
      list = undefined;
    }
  };

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    if (fence.test(line)) {
      endParagraph();
      endList();
      const code: string[] = [];
      for (i++; i < lines.length && !fence.test(lines[i]!); i++) {
        code.push(lines[i]!);
      }
      blocks.push({ kind: "code", text: code.join("\n") });
      continue;
    }
    if (line.trim() === "") {
      endParagraph();
      endList();
      continue;
    }
    const item = bullet.exec(line) ?? numbered.exec(line);
    if (item !== null) {
      endParagraph();
      const ordered = bullet.exec(line) === null;
      if (list !== undefined && list.ordered !== ordered) {
        endList();
      }
      list ??= { ordered, items: [] };
      list.items.push(item[1]!);
      continue;
    }
    if (list !== undefined) {
      // a line under an item continues it
      list.items[list.items.length - 1] += " " + line.trim();
      continue;
    }
    paragraph.push(line.trim());
  }
  endParagraph();
  endList();
  return blocks;
}

const wordChar = /[\p{L}\p{N}]/u;
const link = /^\[([^\]\n]+)\]\((https?:\/\/[^\s()]+)\)/i;

export function parseInline(text: string): Inline[] {
  const out: Inline[] = [];
  let buffer = "";
  const flush = (): void => {
    if (buffer.length > 0) {
      out.push({ kind: "text", text: buffer });
      buffer = "";
    }
  };

  let i = 0;
  while (i < text.length) {
    const c = text[i]!;
    if (c === "\n") {
      flush();
      out.push({ kind: "break" });
      i++;
      continue;
    }
    if (c === "`") {
      // a run of backticks closes at the next run of the same length; `` a`b `` drops one space on each side
      const run = /^`+/.exec(text.slice(i))![0];
      const end = text.indexOf(run, i + run.length);
      if (end > i) {
        flush();
        const code = text.slice(i + run.length, end);
        out.push({ kind: "code", text: run.length > 1 && code.startsWith(" ") && code.endsWith(" ") && code.length > 2 ? code.slice(1, -1) : code });
        i = end + run.length;
        continue;
      }
      buffer += run;
      i += run.length;
      continue;
    }
    if (text.startsWith("**", i)) {
      const end = text.indexOf("**", i + 2);
      if (end > i + 2) {
        flush();
        out.push({ kind: "strong", children: parseInline(text.slice(i + 2, end)) });
        i = end + 2;
        continue;
      }
    }
    if ((c === "*" || c === "_") && i + 1 < text.length && text[i + 1] !== " " && text[i + 1] !== c) {
      const end = text.indexOf(c, i + 1);
      // _ marks emphasis only between words, so snake_case stays text
      const bounded = c === "*" || (!wordChar.test(text[i - 1] ?? " ") && !wordChar.test(text[end + 1] ?? " "));
      if (end > i + 1 && text[end - 1] !== " " && bounded) {
        flush();
        out.push({ kind: "em", children: parseInline(text.slice(i + 1, end)) });
        i = end + 1;
        continue;
      }
    }
    if (c === "[") {
      const m = link.exec(text.slice(i));
      if (m !== null) {
        flush();
        out.push({ kind: "link", href: m[2]!, children: parseInline(m[1]!) });
        i += m[0].length;
        continue;
      }
    }
    buffer += c;
    i++;
  }
  flush();
  return out;
}
