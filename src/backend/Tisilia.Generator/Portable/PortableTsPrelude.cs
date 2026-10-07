namespace Tisilia.Generator.Portable;

/// <summary>
/// Scalar primitives of the self-contained portable TypeScript module: exact decimal→binary float rounding,
/// the shortest round-trip float writers, the System.Text.Json calendar/time grammars on 100 ns ticks, TimeSpan "c",
/// char and strict RFC 4648 base64. Ported function by function from the runtime (<c>src/frontend/runtime/src/primitives</c>)
/// so that both sides produce the same domain values; <c>src/frontend/runtime/test/portable-prelude.test.ts</c> checks them
/// against the runtime over a shared corpus.
/// </summary>
internal static class PortableTsPrelude
{
    public const string Scalars = """

        // ---- exact numeric primitives, ported from the runtime primitives/float.ts and bigmath.ts
        const pow10Cache = [1n];
        function pow10(n) {
          while (pow10Cache.length <= n) pow10Cache.push(pow10Cache[pow10Cache.length - 1] * 10n);
          return pow10Cache[n];
        }
        const floatLexeme = /^([+-]?)(\d+)(?:\.(\d+))?(?:[eE]([+-]?\d+))?$/;
        function splitLexeme(text) {
          const m = floatLexeme.exec(text);
          if (m === null) return undefined;
          const negative = m[1] === "-";
          const frac = m[3] ?? "";
          let exp10 = -frac.length;
          if (m[4] !== undefined) {
            if (m[4].replace(/^[+-]/, "").length > 9) return { negative, digits: -1n, exp10: m[4].startsWith("-") ? -1000000000 : 1000000000 };
            exp10 += Number.parseInt(m[4], 10);
          }
          const all = (m[2] + frac).replace(/^0+/, "");
          return { negative, digits: all.length === 0 ? 0n : BigInt(all), exp10 };
        }
        function bitLength(n) {
          if (n < 0n) n = -n;
          return n === 0n ? 0 : n.toString(2).length;
        }
        const binaryFormats = {
          float32: { mantissaBits: 24, minExponent: -149, maxExponent: 128 },
          float64: { mantissaBits: 53, minExponent: -1074, maxExponent: 1024 },
        };
        function roundToBinary(digits, exp10, negative, format) {
          if (digits === 0n) return { value: negative ? -0 : 0, overflow: false };
          const N = exp10 >= 0 ? digits * pow10(exp10) : digits;
          const D = exp10 >= 0 ? 1n : pow10(-exp10);
          const mant = BigInt(format.mantissaBits);
          let e = bitLength(N) - bitLength(D) - format.mantissaBits;
          const compute = (exp) => {
            if (exp >= 0) { const den = D << BigInt(exp); const q = N / den; return { q, rem2: (N - q * den) * 2n, den }; }
            const num = N << BigInt(-exp); const q = num / D; return { q, rem2: (num - q * D) * 2n, den: D };
          };
          let r = compute(e);
          while (r.q >= 1n << mant) { e++; r = compute(e); }
          while (r.q < 1n << (mant - 1n) && e > format.minExponent) { e--; r = compute(e); }
          if (e < format.minExponent) { e = format.minExponent; r = compute(e); }
          let q = r.q;
          if (r.rem2 > r.den || (r.rem2 === r.den && (q & 1n) === 1n)) { q += 1n; if (q === 1n << mant) { q >>= 1n; e++; } }
          if (e + bitLength(q) > format.maxExponent) return { value: negative ? -Infinity : Infinity, overflow: true };
          const magnitude = Number(q) * 2 ** e;
          return { value: negative ? -magnitude : magnitude, overflow: false };
        }
        function parseFloatCore(name, text, path) {
          const parts = splitLexeme(text);
          if (parts === undefined) throw fail("grammar", path, "invalid " + name + " lexeme");
          if (parts.digits < 0n) return parts.exp10 > 0 ? { value: parts.negative ? -Infinity : Infinity, overflow: true } : { value: parts.negative ? -0 : 0, overflow: false };
          if (parts.digits === 0n) return { value: parts.negative ? -0 : 0, overflow: false };
          const estimate = parts.digits.toString(10).length + parts.exp10;
          if (estimate < -400) return { value: parts.negative ? -0 : 0, overflow: false };
          if (estimate > 400) return { value: parts.negative ? -Infinity : Infinity, overflow: true };
          return roundToBinary(parts.digits, parts.exp10, parts.negative, binaryFormats[name]);
        }
        function parseFloatLexeme(name, text, path) {
          const r = parseFloatCore(name, text, path);
          if (r.overflow) throw fail("range", path, name + " value overflows its binary format");
          return r.value;
        }
        function validateFloat(name, value, path) {
          if (typeof value !== "number") throw fail("type-mismatch", path, name + " requires a number");
          if (!Number.isFinite(value)) throw fail("range", path, name + " must be finite (portable writers have no named floating-point literals)");
          if (name === "float32" && Math.fround(value) !== value) throw fail("range", path, "value is not representable as float32");
          return value;
        }
        function formatFloat(name, value, path) {
          if (!Number.isFinite(value)) throw fail("range", path, name + " canonical writer only writes finite values");
          if (value === 0) return Object.is(value, -0) ? "-0" : "0";
          if (name === "float64") return String(value);
          if (Math.fround(value) !== value) throw fail("type-mismatch", path, "value is not exactly representable as float32");
          for (let precision = 1; precision <= 9; precision++) {
            const raw = value.toPrecision(precision);
            const e = raw.indexOf("e");
            let mantissa = e >= 0 ? raw.slice(0, e) : raw;
            const exponent = e >= 0 ? raw.slice(e) : "";
            if (mantissa.includes(".")) mantissa = mantissa.replace(/0+$/, "").replace(/\.$/, "");
            const candidate = mantissa + exponent;
            const parsed = parseFloatCore("float32", candidate, path);
            if (!parsed.overflow && Object.is(parsed.value, value)) return candidate;
          }
          throw fail("range", path, "float32 could not be formatted");
        }

        // ---- char and bytes, ported from primitives/text.ts
        function validateChar(value, path) {
          if (typeof value !== "string" || value.length !== 1) throw fail("type-mismatch", path, "char requires exactly one UTF-16 code unit");
          const c = value.charCodeAt(0);
          if (c >= 0xd800 && c <= 0xdfff) throw fail("domain-rule", path, "char cannot be a surrogate code unit");
          return value;
        }
        const base64Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        const base64Lookup = new Int16Array(128).fill(-1);
        for (let i = 0; i < base64Alphabet.length; i++) base64Lookup[base64Alphabet.charCodeAt(i)] = i;
        function encodeBase64(bytes) {
          let out = "";
          let i = 0;
          for (; i + 2 < bytes.length; i += 3) {
            const n = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
            out += base64Alphabet[n >> 18] + base64Alphabet[(n >> 12) & 63] + base64Alphabet[(n >> 6) & 63] + base64Alphabet[n & 63];
          }
          const rest = bytes.length - i;
          if (rest === 1) { const n = bytes[i] << 16; out += base64Alphabet[n >> 18] + base64Alphabet[(n >> 12) & 63] + "=="; }
          else if (rest === 2) { const n = (bytes[i] << 16) | (bytes[i + 1] << 8); out += base64Alphabet[n >> 18] + base64Alphabet[(n >> 12) & 63] + base64Alphabet[(n >> 6) & 63] + "="; }
          return out;
        }
        function decodeBase64(text, path) {
          if (text.length % 4 !== 0) throw fail("grammar", path, "base64 length must be a multiple of 4 (padding required)");
          if (text.length === 0) return new Uint8Array(0);
          let padding = 0;
          if (text.endsWith("==")) padding = 2;
          else if (text.endsWith("=")) padding = 1;
          const out = new Uint8Array((text.length / 4) * 3 - padding);
          let o = 0;
          for (let i = 0; i < text.length; i += 4) {
            const vals = [];
            for (let k = 0; k < 4; k++) {
              const ch = text.charCodeAt(i + k);
              if (ch === 0x3d) {
                if (i + 4 < text.length || k < 4 - padding) throw fail("grammar", path, "misplaced base64 padding");
                vals.push(0);
                continue;
              }
              const v = ch < 128 ? base64Lookup[ch] : -1;
              if (v < 0) throw fail("grammar", path, "invalid base64 character");
              vals.push(v);
            }
            const n = (vals[0] << 18) | (vals[1] << 12) | (vals[2] << 6) | vals[3];
            const isLast = i + 4 === text.length;
            out[o++] = (n >> 16) & 255;
            if (!isLast || padding < 2) out[o++] = (n >> 8) & 255;
            if (!isLast || padding < 1) out[o++] = n & 255;
            if (isLast) {
              const trailing = padding === 2 ? n & 0xffff : padding === 1 ? n & 0xff : 0;
              if (trailing !== 0) throw fail("grammar", path, "base64 has non-zero trailing bits (non-canonical encoding)");
            }
          }
          return out;
        }
        function validateBytes(value, path) {
          if (!(value instanceof Uint8Array)) throw fail("type-mismatch", path, "bytes require a Uint8Array");
          return value;
        }

        // ---- calendar and time scalars on 100 ns ticks, ported from primitives/datetime.ts
        const ticksPerSecond = 10000000n;
        const ticksPerMinute = 600000000n;
        const ticksPerHour = 36000000000n;
        const ticksPerDay = 864000000000n;
        const maxDateTimeTicks = 3155378975999999999n;
        const daysToMonth365 = [0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334, 365];
        const daysToMonth366 = [0, 31, 60, 91, 121, 152, 182, 213, 244, 274, 305, 335, 366];
        function isLeapYear(year) { return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0); }
        function daysInMonth(year, month) { const t = isLeapYear(year) ? daysToMonth366 : daysToMonth365; return t[month] - t[month - 1]; }
        function isValidDate(year, month, day) {
          return Number.isInteger(year) && year >= 1 && year <= 9999 && Number.isInteger(month) && month >= 1 && month <= 12 && Number.isInteger(day) && day >= 1 && day <= daysInMonth(year, month);
        }
        function daysFromCivil(year, month, day) {
          const y = year - 1;
          const t = isLeapYear(year) ? daysToMonth366 : daysToMonth365;
          return y * 365 + Math.floor(y / 4) - Math.floor(y / 100) + Math.floor(y / 400) + t[month - 1] + day - 1;
        }
        function civilFromDays(days) {
          let n = days;
          const y400 = Math.floor(n / 146097); n -= y400 * 146097;
          let y100 = Math.floor(n / 36524); if (y100 === 4) y100 = 3; n -= y100 * 36524;
          const y4 = Math.floor(n / 1461); n -= y4 * 1461;
          let y1 = Math.floor(n / 365); if (y1 === 4) y1 = 3; n -= y1 * 365;
          const year = y400 * 400 + y100 * 100 + y4 * 4 + y1 + 1;
          const t = isLeapYear(year) ? daysToMonth366 : daysToMonth365;
          let month = 1;
          while (n >= t[month]) month++;
          return { year, month, day: n - t[month - 1] + 1 };
        }
        function twoDigits(s, at) { const a = s.charCodeAt(at) - 48; const b = s.charCodeAt(at + 1) - 48; return a < 0 || a > 9 || b < 0 || b > 9 ? -1 : a * 10 + b; }
        function fourDigits(s, at) { const hi = twoDigits(s, at); const lo = twoDigits(s, at + 2); return hi < 0 || lo < 0 ? -1 : hi * 100 + lo; }
        // YYYY-MM-DD[THH:mm[:ss[.f{1,7}]]][Z|+HH:mm|-HH:mm]: the canonical System.Text.Json subset (uppercase T/Z, colon offsets, at most 7 fraction digits)
        function parseIsoDateTime(s, path, name) {
          const bad = () => { throw fail("grammar", path, "invalid " + name + " lexeme"); };
          if (s.length < 10) bad();
          const year = fourDigits(s, 0);
          if (year < 0 || s.charCodeAt(4) !== 0x2d) bad();
          const month = twoDigits(s, 5);
          if (month < 0 || s.charCodeAt(7) !== 0x2d) bad();
          const day = twoDigits(s, 8);
          if (day < 0 || !isValidDate(year, month, day)) throw fail("range", path, name + " date does not exist");
          let hour = 0, minute = 0, second = 0, fraction = 0, offset = "none", p = 10;
          if (p < s.length) {
            if (s.charCodeAt(p) !== 0x54) bad();
            p++;
            hour = twoDigits(s, p);
            if (hour < 0 || hour > 23 || s.charCodeAt(p + 2) !== 0x3a) bad();
            minute = twoDigits(s, p + 3);
            if (minute < 0 || minute > 59) bad();
            p += 5;
            if (p < s.length && s.charCodeAt(p) === 0x3a) {
              second = twoDigits(s, p + 1);
              if (second < 0 || second > 59) bad();
              p += 3;
              if (p < s.length && s.charCodeAt(p) === 0x2e) {
                p++;
                const start = p;
                while (p < s.length && s.charCodeAt(p) >= 48 && s.charCodeAt(p) <= 57) p++;
                const digits = p - start;
                if (digits < 1 || digits > 7) bad();
                fraction = Number.parseInt(s.slice(start, p).padEnd(7, "0"), 10);
              }
            }
            if (p < s.length) {
              const c = s.charCodeAt(p);
              if (c === 0x5a) { offset = "utc"; p++; }
              else if (c === 0x2b || c === 0x2d) {
                const sign = c === 0x2d ? -1 : 1;
                const oh = twoDigits(s, p + 1);
                if (oh < 0 || s.charCodeAt(p + 3) !== 0x3a) bad();
                const om = twoDigits(s, p + 4);
                if (om < 0 || om > 59) bad();
                p += 6;
                const minutes = sign * (oh * 60 + om);
                if (minutes < -840 || minutes > 840) throw fail("range", path, name + " offset must be within +/-14:00");
                offset = minutes;
              } else bad();
            }
          }
          if (p !== s.length) bad();
          return { year, month, day, hour, minute, second, fraction, offset };
        }
        function ticksOf(p) {
          return BigInt(daysFromCivil(p.year, p.month, p.day)) * ticksPerDay + BigInt(p.hour) * ticksPerHour + BigInt(p.minute) * ticksPerMinute + BigInt(p.second) * ticksPerSecond + BigInt(p.fraction);
        }
        function pad2(n) { return n < 10 ? "0" + String(n) : String(n); }
        function formatFraction(ticks) { const frac = Number(ticks % ticksPerSecond); return frac === 0 ? "" : "." + String(frac).padStart(7, "0").replace(/0+$/, ""); }
        function formatCalendar(ticks) {
          const { year, month, day } = civilFromDays(Number(ticks / ticksPerDay));
          const rem = ticks % ticksPerDay;
          const hour = Number(rem / ticksPerHour);
          const minute = Number((rem % ticksPerHour) / ticksPerMinute);
          const second = Number((rem % ticksPerMinute) / ticksPerSecond);
          return String(year).padStart(4, "0") + "-" + pad2(month) + "-" + pad2(day) + "T" + pad2(hour) + ":" + pad2(minute) + ":" + pad2(second) + formatFraction(ticks);
        }
        function formatOffset(offsetMinutes) { const abs = Math.abs(offsetMinutes); return (offsetMinutes < 0 ? "-" : "+") + pad2(Math.floor(abs / 60)) + ":" + pad2(abs % 60); }
        function checkTicks(ticks, path, name) {
          if (ticks < 0n || ticks > maxDateTimeTicks) throw fail("range", path, name + " is outside the DateTime range");
          return ticks;
        }
        function parseDateOnly(s, path) {
          if (s.length !== 10) throw fail("grammar", path, "date-only must be YYYY-MM-DD");
          const p = parseIsoDateTime(s, path, "date-only");
          return { kind: "date-only", year: p.year, month: p.month, day: p.day };
        }
        function formatDateOnly(d) { return String(d.year).padStart(4, "0") + "-" + pad2(d.month) + "-" + pad2(d.day); }
        function validateDateOnly(value, path) {
          if (typeof value !== "object" || value === null || !isValidDate(value.year, value.month, value.day)) throw fail("type-mismatch", path, "date-only requires a valid {year, month, day}");
          return { kind: "date-only", year: value.year, month: value.month, day: value.day };
        }
        function parseTimeOnly(s, path) {
          const m = /^(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?$/.exec(s);
          if (m === null) throw fail("grammar", path, "time-only must be HH:mm:ss[.fffffff]");
          const h = Number(m[1]); const mi = Number(m[2]); const sec = Number(m[3]);
          if (h > 23 || mi > 59 || sec > 59) throw fail("range", path, "time-only components out of range");
          const frac = m[4] === undefined ? 0n : BigInt(m[4].padEnd(7, "0"));
          return { kind: "time-only", ticks: BigInt(h) * ticksPerHour + BigInt(mi) * ticksPerMinute + BigInt(sec) * ticksPerSecond + frac };
        }
        function formatTimeOnly(t) {
          const hour = Number(t.ticks / ticksPerHour);
          const minute = Number((t.ticks % ticksPerHour) / ticksPerMinute);
          const second = Number((t.ticks % ticksPerMinute) / ticksPerSecond);
          const frac = Number(t.ticks % ticksPerSecond);
          return pad2(hour) + ":" + pad2(minute) + ":" + pad2(second) + (frac === 0 ? "" : "." + String(frac).padStart(7, "0"));
        }
        function validateTimeOnly(value, path) {
          if (typeof value !== "object" || value === null || typeof value.ticks !== "bigint" || value.ticks < 0n || value.ticks >= ticksPerDay) throw fail("type-mismatch", path, "time-only requires ticks within one day");
          return { kind: "time-only", ticks: value.ticks };
        }
        function parseDateTime(s, path) {
          const p = parseIsoDateTime(s, path, "datetime");
          const ticks = checkTicks(ticksOf(p), path, "datetime");
          if (p.offset === "utc") return { kind: "datetime-utc", ticks };
          if (p.offset === "none") return { kind: "datetime-unspecified", ticks };
          return { kind: "datetime-local-wire", ticks, offsetMinutes: p.offset };
        }
        function formatDateTime(value) {
          if (value.kind === "datetime-utc") return formatDateTimeUtc(value);
          if (value.kind === "datetime-unspecified") return formatDateTimeUnspecified(value);
          return formatDateTimeLocalWire(value);
        }
        function validateDateTime(value, path) {
          const kind = typeof value === "object" && value !== null ? value.kind : undefined;
          if (kind === "datetime-utc" || kind === "datetime-unspecified") return validateDateTimeTicks(kind, value, path);
          if (kind === "datetime-local-wire") return validateDateTimeLocalWire(value, path);
          throw fail("type-mismatch", path, "datetime requires a UTC, unspecified or local wire tick record");
        }
        function validateDateTimeRequest(value, path) {
          const result = validateDateTime(value, path);
          if (result.kind === "datetime-local-wire") {
            const utc = result.ticks - BigInt(result.offsetMinutes) * ticksPerMinute;
            if (utc < 0n || utc > maxDateTimeTicks) throw fail("range", path, "datetime request UTC instant is outside the DateTime range");
          }
          return result;
        }
        function parseDateTimeUtc(s, path) {
          const p = parseIsoDateTime(s, path, "datetime-utc");
          if (p.offset !== "utc") throw fail("grammar", path, "datetime-utc requires the Z suffix");
          return { kind: "datetime-utc", ticks: checkTicks(ticksOf(p), path, "datetime-utc") };
        }
        function formatDateTimeUtc(d) { return formatCalendar(d.ticks) + "Z"; }
        function parseDateTimeUnspecified(s, path) {
          const p = parseIsoDateTime(s, path, "datetime-unspecified");
          if (p.offset !== "none") throw fail("grammar", path, "datetime-unspecified must not carry an offset");
          return { kind: "datetime-unspecified", ticks: checkTicks(ticksOf(p), path, "datetime-unspecified") };
        }
        function formatDateTimeUnspecified(d) { return formatCalendar(d.ticks); }
        function parseDateTimeLocalWire(s, path) {
          const p = parseIsoDateTime(s, path, "datetime-local-wire");
          if (typeof p.offset !== "number") throw fail("grammar", path, "datetime-local-wire requires a +HH:mm or -HH:mm offset");
          return { kind: "datetime-local-wire", ticks: checkTicks(ticksOf(p), path, "datetime-local-wire"), offsetMinutes: p.offset };
        }
        function formatDateTimeLocalWire(d) { return formatCalendar(d.ticks) + formatOffset(d.offsetMinutes); }
        function parseDateTimeOffset(s, path) {
          const p = parseIsoDateTime(s, path, "datetime-offset");
          if (p.offset === "none") throw fail("grammar", path, "datetime-offset requires an offset (the server's local offset is never assumed)");
          const offsetMinutes = p.offset === "utc" ? 0 : p.offset;
          const ticks = checkTicks(ticksOf(p), path, "datetime-offset");
          const utc = ticks - BigInt(offsetMinutes) * ticksPerMinute;
          if (utc < 0n || utc > maxDateTimeTicks) throw fail("range", path, "datetime-offset UTC instant is outside the DateTime range");
          return { kind: "datetime-offset", ticks, offsetMinutes };
        }
        function formatDateTimeOffset(d) { return formatCalendar(d.ticks) + formatOffset(d.offsetMinutes); }
        function validateDateTimeOffset(value, path) {
          if (typeof value !== "object" || value === null || typeof value.ticks !== "bigint" || !Number.isInteger(value.offsetMinutes) || value.offsetMinutes < -840 || value.offsetMinutes > 840) throw fail("type-mismatch", path, "datetime-offset requires {ticks: bigint, offsetMinutes}");
          checkTicks(value.ticks, path, "datetime-offset");
          const utc = value.ticks - BigInt(value.offsetMinutes) * ticksPerMinute;
          if (utc < 0n || utc > maxDateTimeTicks) throw fail("range", path, "datetime-offset UTC instant is outside the DateTime range");
          return { kind: "datetime-offset", ticks: value.ticks, offsetMinutes: value.offsetMinutes };
        }
        function validateDateTimeTicks(kind, value, path) {
          if (typeof value !== "object" || value === null || typeof value.ticks !== "bigint" || value.kind !== kind) throw fail("type-mismatch", path, kind + " requires {kind: \"" + kind + "\", ticks: bigint}");
          return { kind, ticks: checkTicks(value.ticks, path, kind) };
        }
        function validateDateTimeLocalWire(value, path) {
          if (typeof value !== "object" || value === null || value.kind !== "datetime-local-wire" || typeof value.ticks !== "bigint" || !Number.isInteger(value.offsetMinutes) || value.offsetMinutes < -840 || value.offsetMinutes > 840) throw fail("type-mismatch", path, "datetime-local-wire requires {kind: \"datetime-local-wire\", ticks, offsetMinutes}");
          return { kind: "datetime-local-wire", ticks: checkTicks(value.ticks, path, "datetime-local-wire"), offsetMinutes: value.offsetMinutes };
        }
        const durationMax = 9223372036854775807n;
        const durationMin = -9223372036854775808n;
        // TimeSpan "c": [-][d.]hh:mm:ss[.fffffff], the fraction always 7 digits when present
        function parseDuration(s, path) {
          const m = /^(-)?(?:(\d{1,8})\.)?(\d{2}):(\d{2}):(\d{2})(?:\.(\d{7}))?$/.exec(s);
          if (m === null) throw fail("grammar", path, "duration must be [-][d.]hh:mm:ss[.fffffff]");
          const days = m[2] === undefined ? 0n : BigInt(m[2]);
          const hours = Number(m[3]); const minutes = Number(m[4]); const seconds = Number(m[5]);
          if (hours > 23 || minutes > 59 || seconds > 59) throw fail("range", path, "duration components out of range");
          const frac = m[6] === undefined ? 0n : BigInt(m[6]);
          let ticks = days * ticksPerDay + BigInt(hours) * ticksPerHour + BigInt(minutes) * ticksPerMinute + BigInt(seconds) * ticksPerSecond + frac;
          if (m[1] === "-") ticks = -ticks;
          if (ticks > durationMax || ticks < durationMin) throw fail("range", path, "duration is outside the Int64 tick range");
          return { kind: "duration", ticks };
        }
        function formatDuration(d) {
          const negative = d.ticks < 0n;
          const abs = negative ? -d.ticks : d.ticks;
          const days = abs / ticksPerDay;
          const rem = abs % ticksPerDay;
          const hours = Number(rem / ticksPerHour);
          const minutes = Number((rem % ticksPerHour) / ticksPerMinute);
          const seconds = Number((rem % ticksPerMinute) / ticksPerSecond);
          const frac = Number(rem % ticksPerSecond);
          return (negative ? "-" : "") + (days === 0n ? "" : String(days) + ".") + pad2(hours) + ":" + pad2(minutes) + ":" + pad2(seconds) + (frac === 0 ? "" : "." + String(frac).padStart(7, "0"));
        }
        function validateDuration(value, path) {
          if (typeof value !== "object" || value === null || typeof value.ticks !== "bigint" || value.ticks > durationMax || value.ticks < durationMin) throw fail("type-mismatch", path, "duration requires Int64 ticks");
          return { kind: "duration", ticks: value.ticks };
        }

        """;
}
