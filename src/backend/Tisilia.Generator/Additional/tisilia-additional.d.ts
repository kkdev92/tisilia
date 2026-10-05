// tisilia-additional 0.1.0-alpha — type declarations of the additional codec module (installed next to tisilia-additional.js).
// The generated models name the domain types (Int128, Half, …: brands over string and number); these declarations describe the
// exports the generated registry binds, plus helpers for preparing values. Self-contained like the module: the context and JSON AST
// types are the structural parts of @kkdev92/tisilia-runtime's CodecContext and JsonValue the module uses.

/** What the codecs read from the runtime's CodecContext: the value's path and the binding's non-secret context entries (numbers=r|w|n). */
export interface CodecContext {
  readonly path: string;
  readonly context?: ReadonlyMap<string, string>;
}

/** The runtime's lossless JSON AST (numbers keep their lexeme). */
export type JsonValue =
  | { readonly kind: "null" }
  | { readonly kind: "boolean"; readonly value: boolean }
  | { readonly kind: "string"; readonly value: string }
  | { readonly kind: "number"; readonly text: string }
  | { readonly kind: "array"; readonly items: readonly JsonValue[] }
  | { readonly kind: "object"; readonly entries: readonly { readonly name: string; readonly value: JsonValue }[] };

interface Validate<T> {
  validateDomain(value: unknown, context: CodecContext): T;
}
interface RequestEncode<T> {
  encodeRequest(value: T, context: CodecContext): JsonValue;
}
interface ResponseDecode<T> {
  decodeResponse(wire: JsonValue, context: CodecContext): T;
}
interface KeyCodec<T> {
  encodeKey(value: T, context: CodecContext): string;
  decodeKey(text: string, context: CodecContext): T;
}
interface RequestInput<T> {
  parseRequestInput(input: string | JsonValue, context: CodecContext): T;
}
/** A wire grammar (role grammar): whether a lexeme or string belongs to the grammar. */
interface Grammar {
  readonly id: string;
  test(text: string): boolean;
}

// Int128 / UInt128 / BigInteger: canonical decimal strings ("-170141183460469231731687303715884105728", "0", "42").
export function int128DomainRule(value: unknown, context: CodecContext): string;
export const int128Validate: Validate<string>;
export const int128RequestEncode: RequestEncode<string>;
export const int128ResponseDecode: ResponseDecode<string>;
export const int128Key: KeyCodec<string>;
export const int128RequestInput: RequestInput<string>;
export function int128Oracle(a: string, b: string): boolean;
export const int128Grammar: Grammar;
export const int128StringGrammar: Grammar;

export function uint128DomainRule(value: unknown, context: CodecContext): string;
export const uint128Validate: Validate<string>;
export const uint128RequestEncode: RequestEncode<string>;
export const uint128ResponseDecode: ResponseDecode<string>;
export const uint128Key: KeyCodec<string>;
export const uint128RequestInput: RequestInput<string>;
export function uint128Oracle(a: string, b: string): boolean;
export const uint128Grammar: Grammar;
export const uint128StringGrammar: Grammar;

/** The longest BigInteger text (sign included) the codec and BigIntegerJsonConverter accept. */
export const bigIntegerMaxLength: number;
export function bigIntegerDomainRule(value: unknown, context: CodecContext): string;
export const bigIntegerValidate: Validate<string>;
export const bigIntegerRequestEncode: RequestEncode<string>;
export const bigIntegerResponseDecode: ResponseDecode<string>;
export const bigIntegerKey: KeyCodec<string>;
export const bigIntegerRequestInput: RequestInput<string>;
export function bigIntegerOracle(a: string, b: string): boolean;
export const bigIntegerGrammar: Grammar;
export const bigIntegerStringGrammar: Grammar;

// Half: numbers exactly representable in IEEE 754 binary16 (NaN and ±Infinity only under AllowNamedFloatingPointLiterals).
/** Whether the number is a binary16 value. */
export function isHalf(value: number): boolean;
/** The nearest binary16 value (ties to even); Infinity beyond 65504 + 8. */
export function halfOf(value: number): number;
/** The text .NET writes for the Half value (shortest round-trip form, e.g. "6E-08", "65500"). */
export function formatHalf(value: number): string;
/** The binary16 value of a JSON number lexeme (exact decimal rounding); ±Infinity on overflow, undefined when not a lexeme. */
export function parseHalfLexeme(text: string): number | undefined;
export function halfDomainRule(value: unknown, context: CodecContext): number;
export const halfValidate: Validate<number>;
export const halfRequestEncode: RequestEncode<number>;
export const halfResponseDecode: ResponseDecode<number>;
export const halfKey: KeyCodec<number>;
export const halfRequestInput: RequestInput<number>;
export function halfOracle(a: number, b: number): boolean;
export const halfGrammar: Grammar;
export const halfNamedGrammar: Grammar;
export const halfStringOrNamedGrammar: Grammar;

// Uri: Uri.OriginalString. Requests are RFC 3986 URI references .NET keeps; responses are any string the server wrote. No key codec:
// Dictionary<Uri, T> compares keys with Uri.Equals (case, escaping and fragments merge keys).
export const uriMaxLength: number;
/** The request rule: an RFC 3986 URI reference (ASCII, percent-encoded). */
export function uriRequestRule(value: unknown, context: CodecContext): string;
export function uriDomainRule(value: unknown, context: CodecContext): string;
export const uriValidate: Validate<string>;
export const uriRequestEncode: RequestEncode<string>;
export const uriResponseDecode: ResponseDecode<string>;
export const uriRequestInput: RequestInput<string>;
export function uriOracle(a: string, b: string): boolean;
export const uriReferenceGrammar: Grammar;

// Version: "major.minor[.build[.revision]]", components 0..2147483647 without leading zeros.
export function versionDomainRule(value: unknown, context: CodecContext): string;
export const versionValidate: Validate<string>;
export const versionRequestEncode: RequestEncode<string>;
export const versionResponseDecode: ResponseDecode<string>;
export const versionKey: KeyCodec<string>;
export const versionRequestInput: RequestInput<string>;
export function versionOracle(a: string, b: string): boolean;
export const versionGrammar: Grammar;

// IPAddress: the canonical text IPAddress.ToString() writes ("192.168.0.1", "2001:db8::1", "fe80::1%3").
/** The canonical text of an IPv6 address given as eight 16-bit words and a scope id. */
export function ipv6Text(words: readonly number[], scope?: number): string;
export function ipAddressDomainRule(value: unknown, context: CodecContext): string;
export const ipAddressValidate: Validate<string>;
export const ipAddressRequestEncode: RequestEncode<string>;
export const ipAddressResponseDecode: ResponseDecode<string>;
export const ipAddressKey: KeyCodec<string>;
export const ipAddressRequestInput: RequestInput<string>;
export function ipAddressOracle(a: string, b: string): boolean;
export const ipAddressGrammar: Grammar;

// Rune: a string holding exactly one Unicode scalar value.
export function runeDomainRule(value: unknown, context: CodecContext): string;
export const runeValidate: Validate<string>;
export const runeRequestEncode: RequestEncode<string>;
export const runeResponseDecode: ResponseDecode<string>;
export const runeKey: KeyCodec<string>;
export const runeRequestInput: RequestInput<string>;
export function runeOracle(a: string, b: string): boolean;
export const runeGrammar: Grammar;

// IPNetwork: the canonical CIDR text IPNetwork.ToString() writes ("10.0.0.0/8", "2001:db8::/32"; no host bits).
export function ipNetworkDomainRule(value: unknown, context: CodecContext): string;
export const ipNetworkValidate: Validate<string>;
export const ipNetworkRequestEncode: RequestEncode<string>;
export const ipNetworkResponseDecode: ResponseDecode<string>;
export const ipNetworkKey: KeyCodec<string>;
export const ipNetworkRequestInput: RequestInput<string>;
export function ipNetworkOracle(a: string, b: string): boolean;
export const ipNetworkGrammar: Grammar;

// Index / Range: C# index syntax as Index.ToString() / Range.ToString() write it ("^3", "1..^2", "0..^0").
export function indexDomainRule(value: unknown, context: CodecContext): string;
export const indexValidate: Validate<string>;
export const indexRequestEncode: RequestEncode<string>;
export const indexResponseDecode: ResponseDecode<string>;
export const indexKey: KeyCodec<string>;
export const indexRequestInput: RequestInput<string>;
export function indexOracle(a: string, b: string): boolean;
export const indexGrammar: Grammar;
export function rangeDomainRule(value: unknown, context: CodecContext): string;
export const rangeValidate: Validate<string>;
export const rangeRequestEncode: RequestEncode<string>;
export const rangeResponseDecode: ResponseDecode<string>;
export const rangeKey: KeyCodec<string>;
export const rangeRequestInput: RequestInput<string>;
export function rangeOracle(a: string, b: string): boolean;
export const rangeGrammar: Grammar;

// Complex: { real, imaginary }, two finite doubles.
export interface ComplexValue {
  readonly real: number;
  readonly imaginary: number;
}
export function complexDomainRule(value: unknown, context: CodecContext): ComplexValue;
export const complexValidate: Validate<ComplexValue>;
export const complexRequestEncode: RequestEncode<ComplexValue>;
export const complexResponseDecode: ResponseDecode<ComplexValue>;
export const complexRequestInput: RequestInput<ComplexValue>;
export function complexOracle(a: ComplexValue, b: ComplexValue): boolean;

// JsonValue (System.Text.Json.Nodes): one JSON string, number (lexeme kept) or boolean.
export type JsonScalar = Extract<JsonValue, { readonly kind: "string" | "number" | "boolean" }>;
export function jsonScalarDomainRule(value: unknown, context: CodecContext): JsonScalar;
export const jsonScalarValidate: Validate<JsonScalar>;
export const jsonScalarRequestEncode: RequestEncode<JsonScalar>;
export const jsonScalarResponseDecode: ResponseDecode<JsonScalar>;
export const jsonScalarRequestInput: RequestInput<JsonScalar>;
export function jsonScalarOracle(a: JsonScalar, b: JsonScalar): boolean;
export const jsonNumberGrammar: Grammar;

// TimeZoneInfo / CultureInfo: ids the exporting server accepts (binding context "zones" / "cultures", JSON arrays). Responses: any id.
export function timeZoneIdDomainRule(value: unknown, context: CodecContext): string;
export function timeZoneIdRequestRule(value: unknown, context: CodecContext): string;
export const timeZoneIdValidate: Validate<string>;
export const timeZoneIdRequestEncode: RequestEncode<string>;
export const timeZoneIdResponseDecode: ResponseDecode<string>;
export const timeZoneIdRequestInput: RequestInput<string>;
export function timeZoneIdOracle(a: string, b: string): boolean;
export function timeZoneIds(context: CodecContext): string[];
export function cultureNameDomainRule(value: unknown, context: CodecContext): string;
export function cultureNameRequestRule(value: unknown, context: CodecContext): string;
export const cultureNameValidate: Validate<string>;
export const cultureNameRequestEncode: RequestEncode<string>;
export const cultureNameResponseDecode: ResponseDecode<string>;
export const cultureNameRequestInput: RequestInput<string>;
export function cultureNameOracle(a: string, b: string): boolean;
export function cultureNames(context: CodecContext): string[];
