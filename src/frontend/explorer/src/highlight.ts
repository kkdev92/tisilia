// Syntax colouring for JSON text the Explorer shows (request bodies, raw responses). Tokens are rendered as text nodes with a class —
// never as HTML — and text that is not JSON stays one plain token.

export interface Token {
  readonly kind: "key" | "string" | "number" | "literal" | "punct" | "space" | "plain";
  readonly text: string;
}

const pattern = /("(?:[^"\\\u0000-\u001f]|\\(?:["\\/bfnrt]|u[0-9a-fA-F]{4}))*")(\s*:)?|(-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?)|(true|false|null)|([{}[\],:])|(\s+)/y;

/** Splits JSON text into coloured tokens; anything the pattern does not recognise ends highlighting (the rest is plain). */
export function highlightJson(text: string, maxLength = 200_000): Token[] {
  if (text.length > maxLength) {
    return [{ kind: "plain", text }];
  }
  const tokens: Token[] = [];
  pattern.lastIndex = 0;
  let index = 0;
  while (index < text.length) {
    pattern.lastIndex = index;
    const m = pattern.exec(text);
    if (m === null) {
      tokens.push({ kind: "plain", text: text.slice(index) });
      break;
    }
    if (m[1] !== undefined) {
      if (m[2] !== undefined) {
        tokens.push({ kind: "key", text: m[1] }, { kind: "punct", text: m[2] });
      } else {
        tokens.push({ kind: "string", text: m[1] });
      }
    } else if (m[3] !== undefined) {
      tokens.push({ kind: "number", text: m[3] });
    } else if (m[4] !== undefined) {
      tokens.push({ kind: "literal", text: m[4] });
    } else if (m[5] !== undefined) {
      tokens.push({ kind: "punct", text: m[5] });
    } else {
      tokens.push({ kind: "space", text: m[6]! });
    }
    index = pattern.lastIndex;
  }
  return tokens;
}
