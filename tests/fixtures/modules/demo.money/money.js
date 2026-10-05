// demo.money — paired TypeScript-side implementation of MoneyJsonConverter (Codec ABI 0.3, browser + node).
// Request wire: decimal string "123.4500". Response wire: {"amount":"123.4500","currency":"JPY"}.
// Domain: { amount: Decimal, currency: string } where Decimal = { sign, coefficient: bigint, scale }.

const decimalPattern = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?$/;

function fail(code, path, message) {
  const e = new Error(message);
  e.name = "CodecError";
  e.code = code;
  e.path = path;
  return e;
}

const decimalMaxCoefficient = 79228162514264337593543950335n; // 2^96 - 1, the System.Decimal coefficient range

function parseDecimal(text, path) {
  if (!decimalPattern.test(text)) throw fail("grammar", path, "money amount must be a fixed-point decimal string");
  const negative = text.startsWith("-");
  const body = negative ? text.slice(1) : text;
  const [int, frac = ""] = body.split(".");
  let coefficient = BigInt((int + frac).replace(/^0+(?=\d)/, ""));
  let scale = frac.length;
  // like decimal.Parse: trailing fraction zeros are dropped when the 96-bit coefficient would overflow
  while (coefficient > decimalMaxCoefficient && scale > 0 && coefficient % 10n === 0n) {
    coefficient /= 10n;
    scale--;
  }
  if (coefficient > decimalMaxCoefficient) throw fail("range", path, "money amount is outside the System.Decimal range");
  return { kind: "decimal", sign: negative && coefficient !== 0n ? -1 : 1, coefficient, scale };
}

function formatDecimal(d, scale) {
  let digits = d.coefficient.toString();
  let s = d.scale;
  while (s < scale) { digits += "0"; s++; }
  if (s > scale) throw fail("domain-rule", "", "money amount has more than " + scale + " fraction digits");
  const padded = digits.padStart(scale + 1, "0");
  const body = scale === 0 ? padded : padded.slice(0, -scale) + "." + padded.slice(-scale);
  return d.sign < 0 && d.coefficient !== 0n ? "-" + body : body;
}

// chapter 09 §4 / ACC11: the fraction digits of the converter instance in effect arrive as the non-secret binding context entry
// "scale" (4 for the type attribute instance, 2 for the MVC profile instance); nothing here guesses a global setting
function scaleOf(context) {
  const text = context.context !== undefined && typeof context.context.get === "function" ? context.context.get("scale") : undefined;
  const scale = text === undefined ? 4 : Number(text);
  return Number.isInteger(scale) && scale >= 0 && scale <= 28 ? scale : 4;
}

export function moneyDomainRule(value, context) {
  if (typeof value !== "object" || value === null) throw fail("type-mismatch", context.path, "Money requires {amount, currency}");
  if (typeof value.amount !== "object" || value.amount === null || value.amount.kind !== "decimal" || typeof value.amount.coefficient !== "bigint") throw fail("type-mismatch", context.path + "/amount", "amount must be a Decimal");
  if (value.currency !== "JPY") throw fail("domain-rule", context.path + "/currency", "this API only handles JPY");
  const scale = scaleOf(context);
  if (value.amount.scale > scale) throw fail("domain-rule", context.path + "/amount", "JPY money keeps at most " + scale + " fraction digits");
  if (value.amount.coefficient > decimalMaxCoefficient) throw fail("range", context.path + "/amount", "money amount is outside the System.Decimal range");
  return { amount: value.amount, currency: "JPY" };
}

export const moneyValidate = { validateDomain: moneyDomainRule };

export const moneyRequestEncode = {
  encodeRequest(value, context) {
    const v = moneyDomainRule(value, context);
    return { kind: "string", value: formatDecimal(v.amount, scaleOf(context)) };
  },
};

export const moneyResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "object") throw fail("type-mismatch", context.path, "Money response must be an object");
    let amount, currency;
    for (const entry of wire.entries) {
      if (entry.name === "amount" && entry.value.kind === "string") amount = parseDecimal(entry.value.value, context.path + "/amount");
      else if (entry.name === "currency" && entry.value.kind === "string") currency = entry.value.value;
      else throw fail("unexpected-property", context.path + "/" + entry.name, "unexpected Money member");
    }
    if (amount === undefined || currency === undefined) throw fail("missing-required", context.path, "Money requires amount and currency");
    return moneyDomainRule({ amount, currency }, context);
  },
};

export const moneyRequestInput = {
  parseRequestInput(input, context) {
    const text = typeof input === "string" ? input.trim() : input.kind === "string" ? input.value : input.kind === "number" ? input.text : null;
    if (text === null) throw fail("type-mismatch", context.path, "enter a decimal amount");
    return { amount: parseDecimal(text, context.path), currency: "JPY" };
  },
};

export function moneyOracle(a, b) {
  return a.currency === b.currency && formatDecimal(a.amount, 4) === formatDecimal(b.amount, 4);
}

// ---------------------------------------------------------------- MoneyCents: the same CLR type under a member-level converter
// (chapter 09 §4 property context): both wires are an integer number of hundredths within Int64; domain = { amount, currency }.

const int64Min = -9223372036854775808n;
const int64Max = 9223372036854775807n;

function toCents(d, path) {
  if (d.scale > 2) throw fail("domain-rule", path + "/amount", "MoneyCents keeps at most 2 fraction digits");
  let cents = d.coefficient;
  for (let s = d.scale; s < 2; s++) cents *= 10n;
  if (d.sign < 0) cents = -cents;
  if (cents < int64Min || cents > int64Max) throw fail("range", path + "/amount", "MoneyCents hundredths are outside the Int64 range");
  return cents;
}

function fromCents(text, path) {
  if (!/^-?(?:0|[1-9][0-9]*)$/.test(text)) throw fail("grammar", path, "MoneyCents must be an integer lexeme");
  const cents = BigInt(text);
  if (cents < int64Min || cents > int64Max) throw fail("range", path, "MoneyCents hundredths are outside the Int64 range");
  return { kind: "decimal", sign: cents < 0n ? -1 : 1, coefficient: cents < 0n ? -cents : cents, scale: 2 };
}

export function moneyCentsDomainRule(value, context) {
  if (typeof value !== "object" || value === null) throw fail("type-mismatch", context.path, "MoneyCents requires {amount, currency}");
  if (typeof value.amount !== "object" || value.amount === null || value.amount.kind !== "decimal" || typeof value.amount.coefficient !== "bigint") throw fail("type-mismatch", context.path + "/amount", "amount must be a Decimal");
  if (value.currency !== "JPY") throw fail("domain-rule", context.path + "/currency", "this API only handles JPY");
  toCents(value.amount, context.path);
  return { amount: value.amount, currency: "JPY" };
}

export const moneyCentsValidate = { validateDomain: moneyCentsDomainRule };

export const moneyCentsRequestEncode = {
  encodeRequest(value, context) {
    const v = moneyCentsDomainRule(value, context);
    return { kind: "number", text: toCents(v.amount, context.path).toString() };
  },
};

export const moneyCentsResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "number") throw fail("type-mismatch", context.path, "MoneyCents response must be a JSON number");
    return { amount: fromCents(wire.text, context.path), currency: "JPY" };
  },
};

export const moneyCentsRequestInput = {
  parseRequestInput(input, context) {
    const text = typeof input === "string" ? input.trim() : input.kind === "number" ? input.text : input.kind === "string" ? input.value : null;
    if (text === null) throw fail("type-mismatch", context.path, "enter a decimal amount");
    const amount = parseDecimal(text, context.path);
    toCents(amount, context.path);
    return { amount, currency: "JPY" };
  },
};

export function moneyCentsOracle(a, b) {
  return a.currency === b.currency && toCents(a.amount, "") === toCents(b.amount, "");
}

// ---------------------------------------------------------------- TaggedInt32: the closed Tagged<int> converter of a JsonConverterFactory
// (chapter 09 §4, ACC12). Wire (both directions): {"tag": string, "value": int32}, both required, unknown/duplicate members rejected.

const int32Min = -2147483648;
const int32Max = 2147483647;

export function taggedInt32DomainRule(value, context) {
  if (typeof value !== "object" || value === null) throw fail("type-mismatch", context.path, "TaggedInt32 requires {tag, value}");
  if (typeof value.tag !== "string") throw fail("type-mismatch", context.path + "/tag", "tag must be a string");
  if (typeof value.value !== "number" || !Number.isInteger(value.value)) throw fail("type-mismatch", context.path + "/value", "value must be an integer");
  if (value.value < int32Min || value.value > int32Max) throw fail("range", context.path + "/value", "value must be within int32");
  return { tag: value.tag, value: value.value };
}

export const taggedInt32Validate = { validateDomain: taggedInt32DomainRule };

export const taggedInt32RequestEncode = {
  encodeRequest(value, context) {
    const v = taggedInt32DomainRule(value, context);
    return { kind: "object", entries: [{ name: "tag", value: { kind: "string", value: v.tag } }, { name: "value", value: { kind: "number", text: String(v.value) } }] };
  },
};

export const taggedInt32ResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "object") throw fail("type-mismatch", context.path, "TaggedInt32 response must be an object");
    let tag, value;
    for (const entry of wire.entries) {
      if (entry.name === "tag" && tag === undefined && entry.value.kind === "string") tag = entry.value.value;
      else if (entry.name === "value" && value === undefined && entry.value.kind === "number" && /^-?(?:0|[1-9][0-9]*)$/.test(entry.value.text)) value = Number(entry.value.text);
      else throw fail("unexpected-property", context.path + "/" + entry.name, "unexpected or repeated TaggedInt32 member");
    }
    if (tag === undefined || value === undefined) throw fail("missing-required", context.path, "TaggedInt32 requires tag and value");
    return taggedInt32DomainRule({ tag, value }, context);
  },
};

export const taggedInt32RequestInput = {
  parseRequestInput(input, context) {
    // Explorer text form "tag:value"
    const text = typeof input === "string" ? input.trim() : input.kind === "string" ? input.value : null;
    if (text === null) throw fail("type-mismatch", context.path, "enter tag:value");
    const colon = text.lastIndexOf(":");
    if (colon < 0 || !/^-?(?:0|[1-9][0-9]*)$/.test(text.slice(colon + 1))) throw fail("grammar", context.path, "enter tag:value with an integer value");
    return taggedInt32DomainRule({ tag: text.slice(0, colon), value: Number(text.slice(colon + 1)) }, context);
  },
};

export function taggedInt32Oracle(a, b) {
  return a.tag === b.tag && a.value === b.value;
}

// ---------------------------------------------------------------- LabelsUpdate.labels Populate behavior (chapter 07 §4, ACC14)
// The server's LabelsUpdate initializes labels with ["default"] and System.Text.Json populates (appends to) that list, so the
// value the server constructs differs from the received JSON (E01). The effect is registered as a normalized behavior whose
// projection maps the received domain AST to the constructed one; the .NET side implements the same projection.

export const labelsPopulateProjection = {
  project(domain, context) {
    if (domain.kind !== "object") throw fail("type-mismatch", context.path, "LabelsUpdate projection requires an object");
    // absent: the initialized list stays as constructed; null: the setter replaced the list with null; array: appended after the initial item
    const entries = domain.entries.map((entry) => {
      if (entry.name !== "labels") return entry;
      if (entry.value.kind === "null") return entry;
      if (entry.value.kind !== "array") throw fail("type-mismatch", context.path + "/labels", "labels must be an array or null");
      return { name: "labels", value: { kind: "array", items: [{ kind: "string", value: "default" }, ...entry.value.items] } };
    });
    if (!entries.some((entry) => entry.name === "labels")) {
      entries.push({ name: "labels", value: { kind: "array", items: [{ kind: "string", value: "default" }] } });
    }
    return { kind: "object", entries };
  },
};

// ---------------------------------------------------------------- DraftNote.note initializer behavior (chapter 07 §4)
// The server initializes note with "draft": an omitted member keeps it (the constructed value differs from the received JSON),
// null and strings replace it. The projection maps the received domain AST to the constructed one; .NET implements the same.

export const draftNoteProjection = {
  project(domain, context) {
    if (domain.kind !== "object") throw fail("type-mismatch", context.path, "DraftNote projection requires an object");
    if (domain.entries.some((entry) => entry.name === "note")) return domain;
    return { kind: "object", entries: [...domain.entries, { name: "note", value: { kind: "string", value: "draft" } }] };
  },
};

// ---------------------------------------------------------------- TaggedString: the second closed converter of the same factory
// Wire (both directions): {"tag": string, "value": string}, both required, unknown/duplicate members rejected.

export function taggedStringDomainRule(value, context) {
  if (typeof value !== "object" || value === null) throw fail("type-mismatch", context.path, "TaggedString requires {tag, value}");
  if (typeof value.tag !== "string") throw fail("type-mismatch", context.path + "/tag", "tag must be a string");
  if (typeof value.value !== "string") throw fail("type-mismatch", context.path + "/value", "value must be a string");
  return { tag: value.tag, value: value.value };
}

export const taggedStringValidate = { validateDomain: taggedStringDomainRule };

export const taggedStringRequestEncode = {
  encodeRequest(value, context) {
    const v = taggedStringDomainRule(value, context);
    return { kind: "object", entries: [{ name: "tag", value: { kind: "string", value: v.tag } }, { name: "value", value: { kind: "string", value: v.value } }] };
  },
};

export const taggedStringResponseDecode = {
  decodeResponse(wire, context) {
    if (wire.kind !== "object") throw fail("type-mismatch", context.path, "TaggedString response must be an object");
    let tag, value;
    for (const entry of wire.entries) {
      if (entry.name === "tag" && tag === undefined && entry.value.kind === "string") tag = entry.value.value;
      else if (entry.name === "value" && value === undefined && entry.value.kind === "string") value = entry.value.value;
      else throw fail("unexpected-property", context.path + "/" + entry.name, "unexpected or repeated TaggedString member");
    }
    if (tag === undefined || value === undefined) throw fail("missing-required", context.path, "TaggedString requires tag and value");
    return taggedStringDomainRule({ tag, value }, context);
  },
};

export const taggedStringRequestInput = {
  parseRequestInput(input, context) {
    // Explorer text form "tag:value"
    const text = typeof input === "string" ? input.trim() : input.kind === "string" ? input.value : null;
    if (text === null) throw fail("type-mismatch", context.path, "enter tag:value");
    const colon = text.indexOf(":");
    if (colon < 0) throw fail("grammar", context.path, "enter tag:value");
    return taggedStringDomainRule({ tag: text.slice(0, colon), value: text.slice(colon + 1) }, context);
  },
};

export function taggedStringOracle(a, b) {
  return a.tag === b.tag && a.value === b.value;
}

// ---------------------------------------------------------------- TagsPatch (type-level Populate, chapter 07 §4): tags → ["seed", …received]

export const tagsPopulateProjection = {
  project(domain, context) {
    if (domain.kind !== "object") throw fail("type-mismatch", context.path, "TagsPatch projection requires an object");
    const entries = domain.entries.map((entry) => {
      if (entry.name !== "tags") return entry;
      if (entry.value.kind === "null") return entry;
      if (entry.value.kind !== "array") throw fail("type-mismatch", context.path + "/tags", "tags must be an array or null");
      return { name: "tags", value: { kind: "array", items: [{ kind: "string", value: "seed" }, ...entry.value.items] } };
    });
    if (!entries.some((entry) => entry.name === "tags")) {
      entries.push({ name: "tags", value: { kind: "array", items: [{ kind: "string", value: "seed" }] } });
    }
    return { kind: "object", entries };
  },
};

// ---------------------------------------------------------------- AuditRecord write-side behaviors (chapter 07 §3/§4)
// The Actor getter writes the stored value upper-cased; the Secret member has a ShouldSerialize predicate (written only when
// includeSecret is true). Both projections map the generated domain value to what the server writes.

export const auditActorProjection = {
  project(domain, context) {
    if (domain.kind !== "object") throw fail("type-mismatch", context.path, "AuditRecord projection requires an object");
    return {
      kind: "object",
      // ASCII letters only, exactly as the .NET getter (toUpperCase would apply the full Unicode mapping of this runtime's ICU)
      entries: domain.entries.map((entry) => (entry.name === "actor" && entry.value.kind === "string" ? { name: "actor", value: { kind: "string", value: entry.value.value.replace(/[a-z]/g, (c) => String.fromCharCode(c.charCodeAt(0) - 32)) } } : entry)),
    };
  },
};

export const auditSecretProjection = {
  project(domain, context) {
    if (domain.kind !== "object") throw fail("type-mismatch", context.path, "AuditRecord projection requires an object");
    const include = domain.entries.some((entry) => entry.name === "includeSecret" && entry.value.kind === "boolean" && entry.value.value === true);
    return { kind: "object", entries: domain.entries.filter((entry) => include || entry.name !== "secret") };
  },
};

// ---------------------------------------------------------------- ShapeFeatures.readOnly getter (chapter 07 §4): computed as name + "!"

export const shapeReadOnlyProjection = {
  project(domain, context) {
    if (domain.kind !== "object") throw fail("type-mismatch", context.path, "ShapeFeatures projection requires an object");
    const nameEntry = domain.entries.find((entry) => entry.name === "name");
    const name = nameEntry !== undefined && nameEntry.value.kind === "string" ? nameEntry.value.value : "";
    return { kind: "object", entries: domain.entries.map((entry) => (entry.name === "readOnly" ? { name: "readOnly", value: { kind: "string", value: name + "!" } } : entry)) };
  },
};
