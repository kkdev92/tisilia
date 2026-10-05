// Type declarations shipped next to the paired module artifact (the generated registry imports money.js).
import type { CodecContext, Decimal, JsonValue } from "@kkdev92/tisilia-runtime";

export interface Money {
  readonly amount: Decimal;
  readonly currency: string;
}

export function moneyDomainRule(value: unknown, context: CodecContext): Money;
export const moneyValidate: { validateDomain(value: unknown, context: CodecContext): Money };
export const moneyRequestEncode: { encodeRequest(value: Money, context: CodecContext): JsonValue };
export const moneyResponseDecode: { decodeResponse(wire: JsonValue, context: CodecContext): Money };
export const moneyRequestInput: { parseRequestInput(input: string | JsonValue, context: CodecContext): Money };
export function moneyOracle(a: Money, b: Money): boolean;

export interface TaggedString {
  readonly tag: string;
  readonly value: string;
}

export function taggedStringDomainRule(value: unknown, context: CodecContext): TaggedString;
export const taggedStringValidate: { validateDomain(value: unknown, context: CodecContext): TaggedString };
export const taggedStringRequestEncode: { encodeRequest(value: TaggedString, context: CodecContext): JsonValue };
export const taggedStringResponseDecode: { decodeResponse(wire: JsonValue, context: CodecContext): TaggedString };
export const taggedStringRequestInput: { parseRequestInput(input: string | JsonValue, context: CodecContext): TaggedString };
export function taggedStringOracle(a: TaggedString, b: TaggedString): boolean;

export const tagsPopulateProjection: { project(domain: JsonValue, context: CodecContext): JsonValue };
export const draftNoteProjection: { project(domain: JsonValue, context: CodecContext): JsonValue };
export const auditActorProjection: { project(domain: JsonValue, context: CodecContext): JsonValue };
export const auditSecretProjection: { project(domain: JsonValue, context: CodecContext): JsonValue };
export const shapeReadOnlyProjection: { project(domain: JsonValue, context: CodecContext): JsonValue };
