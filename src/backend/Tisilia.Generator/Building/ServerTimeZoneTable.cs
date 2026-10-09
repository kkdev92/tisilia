using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Tisilia.Contract;
using Tisilia.Generator.Canonical;
using Tisilia.Generator.Validation;

namespace Tisilia.Generator.Building;

/// <summary>From <see cref="StartTicks"/> (UTC ticks) until the next entry, the zone's UTC offset is <see cref="OffsetMinutes"/>.</summary>
public readonly record struct ServerTimeZoneOffset(long StartTicks, int OffsetMinutes);

/// <summary>
/// The UTC offset of the server's time zone at every instant from <see cref="FromTicks"/> to <see cref="UntilTicks"/>, as .NET computes it.
/// System.Text.Json reads a DateTime written with an offset as <c>DateTimeOffset.LocalDateTime</c>: the UTC ticks plus the offset the
/// server's local zone has at that instant (DateTime.ToLocalTime and TimeZoneInfo.GetUtcOffsetFromUtc, System.Private.CoreLib v10.0.0),
/// and DateTime dictionary keys are equal when their ticks are. Keys a client writes with different offsets can therefore be one key on
/// the server, and the client finds which ones with this table.
/// </summary>
public sealed class ServerTimeZoneTable
{
    /// <summary>The binding context entry that carries the table.</summary>
    public const string ContextName = "serverTimeZone";

    /// <summary>The binding of the builtin DateTime codecs to the server's time zone.</summary>
    public const string BindingId = "std.datetime.server-time-zone";

    public static readonly long FromTicks = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    public static readonly long UntilTicks = new DateTime(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    private static readonly ConditionalWeakTable<TimeZoneInfo, ServerTimeZoneTable> Cache = new();

    private ServerTimeZoneTable(string zoneId, IReadOnlyList<ServerTimeZoneOffset> offsets)
    {
        ZoneId = zoneId;
        Offsets = offsets;
    }

    public string ZoneId { get; }

    /// <summary>Ascending; the first entry starts at <see cref="FromTicks"/>, and consecutive entries have different offsets.</summary>
    public IReadOnlyList<ServerTimeZoneOffset> Offsets { get; }

    /// <summary>The table of a zone, read from <see cref="TimeZoneInfo.GetUtcOffset(DateTimeOffset)"/> (computed once per instance).</summary>
    public static ServerTimeZoneTable Of(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return Cache.GetValue(zone, Build);
    }

    /// <summary>The offset the table gives an instant in its range.</summary>
    public int OffsetMinutesAt(long utcTicks)
    {
        if (utcTicks < FromTicks || utcTicks >= UntilTicks)
        {
            throw new ArgumentOutOfRangeException(nameof(utcTicks), "the instant is outside the table's range");
        }

        int lo = 0, hi = Offsets.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Offsets[mid].StartTicks <= utcTicks) { lo = mid; } else { hi = mid - 1; }
        }

        return Offsets[lo].OffsetMinutes;
    }

    /// <summary>Whether two tables give every instant of the range the same offset.</summary>
    public bool HasSameOffsets(ServerTimeZoneTable other) => Offsets.SequenceEqual(other.Offsets);

    /// <summary><c>{"zone":…,"until":"ticks","offsets":[["ticks",minutes],…]}</c>: ticks as strings, which JavaScript numbers cannot hold.</summary>
    public string ToContextValue()
    {
        var text = new StringBuilder();
        text.Append("{\"zone\":").Append(JsonSerializer.Serialize(ZoneId)).Append(",\"until\":\"").Append(UntilTicks.ToString(CultureInfo.InvariantCulture)).Append("\",\"offsets\":[");
        for (var i = 0; i < Offsets.Count; i++)
        {
            text.Append(i == 0 ? "[\"" : ",[\"").Append(Offsets[i].StartTicks.ToString(CultureInfo.InvariantCulture)).Append("\",")
                .Append(Offsets[i].OffsetMinutes.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        return text.Append("]}").ToString();
    }

    /// <summary>The converter binding that carries the table to the builtin DateTime codecs.</summary>
    public Binding ToBinding()
    {
        var value = ToContextValue();
        return new Binding
        {
            Id = BindingId,
            Kind = BindingKind.Converter,
            Version = "0.1.0",
            Implementation = new BuiltinImpl { Id = Builtins.BindingFor("datetime") },
            SettingsDigest = TisiliaHash.Sha256OfBytes(Encoding.UTF8.GetBytes("server-time-zone:" + value)),
            DependencyIds = [],
            Context = [new BindingContextEntry { Name = ContextName, Value = value, Confidential = false }],
        };
    }

    private static ServerTimeZoneTable Build(TimeZoneInfo zone)
    {
        // An offset changes at the zone's transitions, which an hourly grid brackets, and also at instants that GetUtcOffsetFromUtc derives
        // from a local boundary and one of the zone's offsets: it picks the rule and the year from the instant plus the base offset and
        // compares the instant with daylight bounds computed per year, so some zones give the last tick of a year another offset than its
        // neighbors (Yukon Standard Time at 2021-01-01T06:59:59.9999999Z on Windows). Every such candidate and its neighbors are evaluated
        // too, and between two evaluated instants whose offsets differ, bisection finds each first tick of a new offset.
        var gridCount = (int)((UntilTicks - FromTicks) / TimeSpan.TicksPerHour);
        var grid = new int[gridCount];
        var offsets = new HashSet<long> { zone.BaseUtcOffset.Ticks };
        for (var i = 0; i < gridCount; i++)
        {
            var offset = OffsetAt(zone, FromTicks + (i * TimeSpan.TicksPerHour));
            grid[i] = Minutes(zone, offset);
            offsets.Add(offset);
        }

        var rules = zone.GetAdjustmentRules();
        foreach (var rule in rules)
        {
            offsets.Add(zone.BaseUtcOffset.Ticks + rule.BaseUtcOffsetDelta.Ticks);
            offsets.Add(zone.BaseUtcOffset.Ticks + rule.BaseUtcOffsetDelta.Ticks + rule.DaylightDelta.Ticks);
        }

        var firstYear = new DateTime(FromTicks).Year - 1;
        var lastYear = new DateTime(UntilTicks).Year + 1;
        var locals = new List<long>();
        for (var year = firstYear; year <= lastYear; year++)
        {
            locals.Add(new DateTime(year, 1, 1).Ticks);
        }

        foreach (var rule in rules)
        {
            if (rule.DateEnd.Year < firstYear || rule.DateStart.Year > lastYear)
            {
                continue;
            }

            locals.Add(rule.DateStart.Date.Ticks);
            if (rule.DateEnd.Year < 9999)
            {
                locals.Add(rule.DateEnd.Date.AddDays(1).Ticks);
            }

            for (var year = Math.Max(firstYear, rule.DateStart.Year); year <= Math.Min(lastYear, rule.DateEnd.Year); year++)
            {
                foreach (var transition in new[] { rule.DaylightTransitionStart, rule.DaylightTransitionEnd })
                {
                    if (TransitionLocalTicks(year, transition) is { } local)
                    {
                        locals.Add(local);
                    }
                }
            }
        }

        var candidates = new HashSet<long>();
        foreach (var local in locals)
        {
            foreach (var offset in offsets)
            {
                for (var delta = -1L; delta <= 1; delta++)
                {
                    var t = local - offset + delta;
                    if (t > FromTicks && t < UntilTicks - 1 && (t - FromTicks) % TimeSpan.TicksPerHour != 0)
                    {
                        candidates.Add(t);
                    }
                }
            }
        }

        var ordered = candidates.Order().ToArray();
        var table = new List<ServerTimeZoneOffset> { new(FromTicks, grid[0]) };
        var previousTicks = FromTicks;
        var previousMinutes = grid[0];
        void Step(long ticks, int minutes)
        {
            while (minutes != previousMinutes)
            {
                long lo = previousTicks, hi = ticks;
                while (hi - lo > 1)
                {
                    var mid = lo + ((hi - lo) / 2);
                    if (Minutes(zone, OffsetAt(zone, mid)) == previousMinutes) { lo = mid; } else { hi = mid; }
                }

                previousMinutes = Minutes(zone, OffsetAt(zone, hi));
                table.Add(new ServerTimeZoneOffset(hi, previousMinutes));
                previousTicks = hi;
            }

            previousTicks = ticks;
        }

        // the grid and the candidates in ascending order (a candidate is never a grid instant)
        for (int g = 1, c = 0; g < gridCount || c < ordered.Length;)
        {
            var gridTicks = g < gridCount ? FromTicks + (g * TimeSpan.TicksPerHour) : long.MaxValue;
            if (c < ordered.Length && ordered[c] < gridTicks)
            {
                Step(ordered[c], Minutes(zone, OffsetAt(zone, ordered[c])));
                c++;
            }
            else
            {
                Step(gridTicks, grid[g]);
                g++;
            }
        }

        Step(UntilTicks - 1, Minutes(zone, OffsetAt(zone, UntilTicks - 1)));
        return new ServerTimeZoneTable(zone.Id, table);
    }

    private static long OffsetAt(TimeZoneInfo zone, long utcTicks) => zone.GetUtcOffset(new DateTimeOffset(utcTicks, TimeSpan.Zero)).Ticks;

    private static int Minutes(TimeZoneInfo zone, long offsetTicks) => offsetTicks % TimeSpan.TicksPerMinute == 0
        ? (int)(offsetTicks / TimeSpan.TicksPerMinute)
        : throw new InvalidOperationException($"time zone '{zone.Id}' has an offset that is not a whole number of minutes ({TimeSpan.FromTicks(offsetTicks)}), which DateTimeOffset cannot carry");

    // TimeZoneInfo.TransitionTimeToDateTime (internal, System.Private.CoreLib v10.0.0); null for the empty transition of a rule without one
    private static long? TransitionLocalTicks(int year, TimeZoneInfo.TransitionTime transition)
    {
        if (transition.Month is < 1 or > 12)
        {
            return null;
        }

        var timeOfDay = transition.TimeOfDay.TimeOfDay;
        if (transition.IsFixedDateRule)
        {
            return (new DateTime(year, transition.Month, Math.Min(transition.Day, DateTime.DaysInMonth(year, transition.Month))) + timeOfDay).Ticks;
        }

        if (transition.Week <= 4)
        {
            var first = new DateTime(year, transition.Month, 1) + timeOfDay;
            var delta = (int)transition.DayOfWeek - (int)first.DayOfWeek;
            if (delta < 0)
            {
                delta += 7;
            }

            return first.AddDays(delta + (7 * (transition.Week - 1))).Ticks;
        }

        var last = new DateTime(year, transition.Month, DateTime.DaysInMonth(year, transition.Month)) + timeOfDay;
        var back = (int)last.DayOfWeek - (int)transition.DayOfWeek;
        if (back < 0)
        {
            back += 7;
        }

        return last.AddDays(-back).Ticks;
    }
}
