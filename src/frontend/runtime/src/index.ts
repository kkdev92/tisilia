// @kkdev92/tisilia-runtime — the Tisilia runtime (ESM, TypeScript 6+, ES2022).
// Lossless JSON, precision-preserving primitives, Codec ABI 0.1, HTTP transport, request identity, envelopes.

export type { JsonValue, JsonNull, JsonBoolean, JsonString, JsonNumber, JsonArray, JsonObject, JsonEntry, JsonToken } from "./json/ast.js";
export { jsonNull, jsonBoolean, jsonString, jsonNumber, jsonArray, jsonObject, jsonEntry, findEntry, jsonEquals, fromNative, isNumberLexeme } from "./json/ast.js";
export { parseJson, parseJsonBytes, decodeUtf8Strict, JsonParseError } from "./json/parser.js";
export type { ParseOptions, JsonParseErrorCode } from "./json/parser.js";
export { writeJson, writeJsonBytes } from "./json/writer.js";
export { canonicalize, JcsError } from "./json/jcs.js";
export { defaultLimits, resolveLimits, validateLimits } from "./json/limits.js";
export type { Limits } from "./json/limits.js";

export { CodecError, asCodecError } from "./codec/errors.js";
export type { CodecErrorCode } from "./codec/errors.js";
export { createCodecContext, requireRequest, requireResponse, withContext } from "./codec/abi.js";
export type { Codec, CodecContext, CodecContextOptions, RequestCodec, ResponseCodec, RequestKeyCodec, ResponseKeyCodec, RequestInputCodec, DomainValidator } from "./codec/abi.js";
export { scalarCodec, strictNumbers, webNumbers } from "./codec/scalars.js";
export type { ScalarName, ScalarOptions, NumberProfile } from "./codec/scalars.js";
export { objectCodec, arrayCodec, mapCodec, nullableCodec, enumCodec, taggedUnionCodec, tokenUnionCodec, brandCodec } from "./codec/structural.js";
export type { CodecRef, ObjectCodecDescriptor, PropertyDescriptor, ArrayCodecDescriptor, MapCodecDescriptor, EnumCodecDescriptor, EnumMember, TaggedUnionDescriptor, TaggedUnionVariant, TokenUnionBranch, Presence, NameMatching, DuplicatePolicy, AdditionalPolicy } from "./codec/structural.js";
export { xmlTextCodec, xmlEnumCodec, xmlElementCodec, xmlItemsCodec, isXmlCodec, isNil, readXmlBody, writeXmlBody } from "./codec/xml.js";
export type { XmlCodec, XmlContent, XmlTextCodecDescriptor, XmlEnumCodecDescriptor, XmlElementCodecDescriptor, XmlItemsCodecDescriptor, XmlMemberDescriptor, XmlRoot } from "./codec/xml.js";
export { parseXml, parseXmlBytes, XmlParseError } from "./xml/parser.js";
export type { XmlParseOptions, XmlParseErrorCode } from "./xml/parser.js";
export { writeXml } from "./xml/writer.js";
export type { XmlElement, XmlAttribute, XmlText, XmlChild } from "./xml/dom.js";
export { formatXmlDuration, parseXmlDuration } from "./xml/lexical.js";
export type { XmlScalarGrammar, XmlEnumName } from "./xml/lexical.js";
export { TisiliaMap, normalizeKey } from "./codec/map.js";
export type { KeyComparer } from "./codec/map.js";
export { CodecRegistry } from "./codec/registry.js";

export type { Int64, UInt64, IntegerScalarName } from "./primitives/integers.js";
export { int64, uint64, integerRanges } from "./primitives/integers.js";
export type { Decimal } from "./primitives/decimal.js";
export { decimal, decimalFromString, formatDecimal, parseDecimalLexeme, compareDecimal, decimalEquals, isDecimal } from "./primitives/decimal.js";
export { parseFloat32Lexeme, parseFloat64Lexeme, formatFloat32, formatFloat64, formatDotnetShortest, isFloat32Value } from "./primitives/float.js";
export type { Guid } from "./primitives/text.js";
export { guid, parseGuid, encodeBase64, decodeBase64, isWellFormedUnicode } from "./primitives/text.js";
export { ordinalUpper, ordinalUpperCodePoint, ordinalEqualsIgnoreCase } from "./primitives/ordinalCasing.js";
export type { DateTime, DateOnly, TimeOnly, DateTimeUtc, DateTimeUnspecified, DateTimeLocalWire, DateTimeOffset, Duration } from "./primitives/datetime.js";
export {
  parseDateOnly,
  formatDateOnly,
  parseTimeOnly,
  formatTimeOnly,
  parseDateTime,
  formatDateTime,
  parseDateTimeUtc,
  formatDateTimeUtc,
  parseDateTimeUnspecified,
  formatDateTimeUnspecified,
  parseDateTimeLocalWire,
  formatDateTimeLocalWire,
  parseDateTimeOffset,
  formatDateTimeOffset,
  dateTimeOffsetToDate,
  dateTimeOffsetFromDate,
  dateTimeUtcToDate,
  dateTimeUtcFromDate,
  parseDuration,
  formatDuration,
  daysFromCivil,
  civilFromDays,
  isValidDate,
  ticksPerDay,
  ticksPerSecond,
} from "./primitives/datetime.js";

export { parseMediaType, jsonMediaEssences, essenceOf } from "./http/mediaType.js";
export type { MediaType } from "./http/mediaType.js";
export { buildUrl, encodePathSegment, encodeQueryComponent, validateBaseUrl, validateHeaderName, validateHeaderValue, isForbiddenRequestHeader } from "./http/url.js";
export type { BuiltUrl, QueryEntry } from "./http/url.js";
export { buildPlannedUrl, displayRoute } from "./http/routes.js";
export type { RoutePlan, RouteParameter, ResolvedRoutePart } from "./http/routes.js";
export type { BufferedFile, StreamedFile, DownloadSink, DownloadResult } from "./http/client.js";
export { suggestedFileName, safeFileName } from "./http/filename.js";
export { standardBinder, codecBinder, enumBinder, formatScalarForBinding, formatScalarForRequestCulture } from "./http/binders.js";
export type { Binder, ParameterLocation, NullPolicy, StandardBinderOptions, EnumBinderOptions } from "./http/binders.js";
export { send } from "./http/transport.js";
export type { TransportRequest, TransportResponse, TransportOutcome, TransportOptions } from "./http/transport.js";
export { execute, executeWithRaw, download, subscribe, prepareRequest, fetchResponse, decodeResponse } from "./http/client.js";
export { supportsRequestStreams } from "./http/transport.js";
export type { EventSink, SubscriptionResult } from "./http/client.js";
export type { ServerSentEvent } from "./http/sse.js";
export type { UploadFile, FormFieldDescriptor } from "./http/forms.js";
export type { FormRequestBodyDescriptor } from "./http/client.js";
export type { OperationDescriptor, ParameterDescriptor, RequestBodyDescriptor, JsonRequestBodyDescriptor, BinaryRequestBodyDescriptor, XmlRequestBodyDescriptor, ResponseBodyDescriptor, ResponseCaseDescriptor, ClientOptions, CredentialProvider, PreparedRequest, OperationResult, HttpMethod, RawOutcome, RawResponse, ReconnectOptions } from "./http/client.js";
export type { RuntimeFailure, UnexpectedResponse, CodecFailure, TransportFailure, Cancelled, Timeout, LimitFailure, ContractMismatch, ResponseCaseResult, BodylessCaseResult, ResponseMetadata } from "./http/result.js";
export { isFailure } from "./http/result.js";

export { createRequestIdentityRecord, computeRequestIdentity, computeRequestIdentityHmac, requestIdentityBytes, createScopeNonce, isRequestIdentity, sha256Hex, requestHashPrefix } from "./identity.js";
export type { RequestIdentityRecord } from "./identity.js";
export { parseHydrationEnvelope, EnvelopeError } from "./envelope.js";
export { createHydrationEnvelope, failureEnvelopeOf, checkHydrationEnvelope, rawFromEnvelope } from "./hydration.js";
export type { EnvelopeContext, EnvelopeCheck, EnvelopeMismatch } from "./hydration.js";
export type { HydrationEnvelope, JsonEnvelope, BodylessEnvelope, TextEnvelope, FailureEnvelope, EnvelopeFailureCode } from "./envelope.js";

export { createContractRegistry, numbersOf, contextFor } from "./contract/interpreter.js";
export type { ContractDocument, ContractRegistry, ContractRegistryOptions, ContractOperation, ContractFormField, ContractCodec, ContractModel, ContractWire, ContractModule, ContractTypeUse, ContractWireRef, ContractXmlMember, ContractXmlName } from "./contract/interpreter.js";

export const runtimeVersion = "0.2.0-alpha";
export const abiVersion = "0.1";
export const httpDescriptorVersion = "0.1";
