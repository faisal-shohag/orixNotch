using System.Globalization;

namespace OrixNotch.Services;

/// <summary>
/// Small iCalendar (RFC 5545) reader for subscribed calendars: each VEVENT becomes local-time occurrences
/// inside a date window. Covers what Google, Outlook and iCloud feeds use: UTC / TZID / floating / all-day
/// starts, RRULE (DAILY, WEEKLY, MONTHLY, YEARLY with INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY), EXDATE,
/// moved or cancelled instances (RECURRENCE-ID) and STATUS:CANCELLED.
/// </summary>
/// <remarks>
/// shortcut: BYMONTH, BYSETPOS, BYWEEKNO, BYYEARDAY, WKST other than Monday, and VTIMEZONE blocks with names
/// Windows doesn't know (those fall back to local time) are not handled. Add them when a real feed needs it.
/// Multi-day events show on their first day only.
/// </remarks>
public static class IcsParser
{
    public sealed record Occurrence(DateTime Start, bool AllDay, string Title);

    private sealed record Prop(string Name, Dictionary<string, string> Params, string Value);

    /// <summary>A start as written: wall-clock time in its own zone (UTC, a TZID, or local when floating).</summary>
    private readonly record struct Stamp(DateTime Wall, TimeZoneInfo Zone, bool AllDay);

    private const int MaxPeriods = 5000; // a runaway rule stops here

    public static List<Occurrence> Parse(string ics, DateTime from, DateTime to)
    {
        var events = ReadEvents(ics);
        var result = new List<Occurrence>();

        // Instances moved or cancelled by a RECURRENCE-ID entry don't come from the master's rule.
        var overridden = new HashSet<(string Uid, DateTime Utc)>();
        foreach (var e in events)
        {
            if (Get(e, "RECURRENCE-ID") is { } rid && Get(e, "UID")?.Value is { } uid && ParseStamp(rid) is { } stamp)
                overridden.Add((uid, ToUtc(stamp)));
        }

        foreach (var e in events)
        {
            if (Get(e, "DTSTART") is not { } dtstart || ParseStamp(dtstart) is not { } start) continue;
            if (Get(e, "STATUS")?.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true) continue;
            var title = Unescape(Get(e, "SUMMARY")?.Value ?? "");
            if (title.Length == 0) title = "(No title)";
            var uid = Get(e, "UID")?.Value ?? "";

            IEnumerable<DateTime> walls = [start.Wall];
            if (Get(e, "RECURRENCE-ID") is null && Get(e, "RRULE") is { } rrule)
            {
                var excluded = e.Where(p => p.Name == "EXDATE")
                    .SelectMany(p => p.Value.Split(',').Select(v => ParseStamp(p with { Value = v })))
                    .OfType<Stamp>()
                    .Select(ToUtc)
                    .ToHashSet();
                walls = Expand(start, rrule.Value, ToWall(from.Date.AddDays(-1), start.Zone), ToWall(to.Date.AddDays(1), start.Zone))
                    .Where(w => !excluded.Contains(ToUtc(start with { Wall = w })) && !overridden.Contains((uid, ToUtc(start with { Wall = w }))));
            }

            foreach (var wall in walls)
            {
                var local = start.AllDay ? wall.Date : ToLocal(start with { Wall = wall });
                if (local.Date >= from.Date && local.Date <= to.Date) result.Add(new Occurrence(local, start.AllDay, title));
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    // ---------------------------------------------------------------- recurrence

    /// <summary>Wall-clock starts of a recurring event between <paramref name="windowStart"/> and <paramref name="windowEnd"/> (roughly: callers filter).</summary>
    private static IEnumerable<DateTime> Expand(Stamp start, string rrule, DateTime windowStart, DateTime windowEnd)
    {
        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);
        var freq = parts.GetValueOrDefault("FREQ", "").ToUpperInvariant();
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var n) && n > 0 ? n : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) ? c : null;
        DateTime? until = parts.GetValueOrDefault("UNTIL") is { } u && ParseStamp(new Prop("UNTIL", new(), u)) is { } us
            ? (us.Zone == TimeZoneInfo.Utc && start.Zone != TimeZoneInfo.Utc ? ToWall(ToUtc(us), start.Zone, utc: true) : us.Wall)
            : null;
        var byDay = parts.GetValueOrDefault("BYDAY")?.Split(',').Select(ParseByDay).Where(b => b.HasValue).Select(b => b!.Value).ToList() ?? [];
        var byMonthDay = parts.GetValueOrDefault("BYMONTHDAY")?.Split(',').Select(s => int.TryParse(s, out var d) ? d : 0).Where(d => d != 0).ToList() ?? [];
        var end = until is { } x && x < windowEnd ? x : windowEnd;
        if (start.AllDay && until is { } ud) end = ud.Date < windowEnd ? ud.Date : windowEnd;

        if (freq is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"))
        {
            yield return start.Wall; // unsupported rule (e.g. HOURLY): just the first one
            yield break;
        }

        // Without COUNT, jump straight to the window: a daily event from years ago mustn't spend the period budget.
        var skip = 0;
        if (count is null && windowStart > start.Wall)
        {
            var gap = windowStart - start.Wall;
            var months = (windowStart.Year - start.Wall.Year) * 12 + windowStart.Month - start.Wall.Month;
            skip = Math.Max(0, freq switch
            {
                "DAILY" => (int)(gap.TotalDays / interval),
                "WEEKLY" => (int)(gap.TotalDays / 7 / interval),
                "MONTHLY" => months / interval,
                _ => months / 12 / interval,
            } - 1);
        }

        var produced = 0;
        for (var k = skip; k < skip + MaxPeriods; k++)
        {
            List<DateTime> candidates = freq switch
            {
                "DAILY" => [start.Wall.AddDays((double)k * interval)],
                "WEEKLY" => WeekCandidates(start.Wall, k * interval, byDay),
                "MONTHLY" => MonthCandidates(start.Wall, k * interval, byDay, byMonthDay),
                _ => YearCandidates(start.Wall, k * interval),
            };
            if (freq == "DAILY" && byDay.Count > 0)
                candidates = candidates.Where(d => byDay.Any(b => b.Day == d.DayOfWeek)).ToList();
            var periodStart = freq switch
            {
                "DAILY" => start.Wall.AddDays((double)k * interval),
                "WEEKLY" => WeekStart(start.Wall).AddDays(7.0 * k * interval),
                "MONTHLY" => FirstOfMonth(start.Wall).AddMonths(k * interval),
                _ => new DateTime(start.Wall.Year, 1, 1).AddYears(k * interval),
            };
            if (periodStart > end) yield break;

            foreach (var d in candidates.OrderBy(d => d))
            {
                if (d < start.Wall) continue;
                if (d > end || count is { } max && produced >= max) yield break;
                produced++;
                yield return d;
            }
        }
    }

    private static List<DateTime> WeekCandidates(DateTime first, int weeks, List<(int Nth, DayOfWeek Day)> byDay)
    {
        var week = WeekStart(first).AddDays(7.0 * weeks);
        if (byDay.Count == 0) return [first.AddDays(7.0 * weeks)];
        return byDay.Select(b => week.AddDays(((int)b.Day + 6) % 7) + first.TimeOfDay).ToList(); // Monday-based week
    }

    private static List<DateTime> MonthCandidates(DateTime first, int months, List<(int Nth, DayOfWeek Day)> byDay, List<int> byMonthDay)
    {
        var month = FirstOfMonth(first).AddMonths(months);
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        var list = new List<DateTime>();
        foreach (var md in byMonthDay)
        {
            var day = md > 0 ? md : days + md + 1;
            if (day >= 1 && day <= days) list.Add(month.AddDays(day - 1));
        }
        foreach (var (nth, dow) in byDay)
        {
            var matches = Enumerable.Range(0, days).Select(i => month.AddDays(i)).Where(d => d.DayOfWeek == dow).ToList();
            if (nth == 0) list.AddRange(matches);
            else if (Math.Abs(nth) <= matches.Count) list.Add(nth > 0 ? matches[nth - 1] : matches[^-nth]);
        }
        if (byMonthDay.Count == 0 && byDay.Count == 0 && first.Day <= days) list.Add(month.AddDays(first.Day - 1)); // 31st: skips short months
        return list.Select(d => d.Date + first.TimeOfDay).ToList();
    }

    private static List<DateTime> YearCandidates(DateTime first, int years)
    {
        var year = first.Year + years;
        if (first.Month == 2 && first.Day == 29 && !DateTime.IsLeapYear(year)) return [];
        return [new DateTime(year, first.Month, first.Day) + first.TimeOfDay];
    }

    private static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    private static DateTime FirstOfMonth(DateTime d) => new(d.Year, d.Month, 1);

    /// <summary>"MO", "2TU", "-1FR" → (ordinal, day); ordinal 0 means every such day.</summary>
    private static (int Nth, DayOfWeek Day)? ParseByDay(string s)
    {
        s = s.Trim();
        if (s.Length < 2) return null;
        DayOfWeek? day = s[^2..].ToUpperInvariant() switch
        {
            "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
            "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
        };
        if (day is null) return null;
        var nth = s.Length > 2 && int.TryParse(s[..^2], out var v) ? v : 0;
        return (nth, day.Value);
    }

    // ---------------------------------------------------------------- times

    private static Stamp? ParseStamp(Prop p)
    {
        var v = p.Value.Trim();
        var allDay = p.Params.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase) || v.Length == 8;
        if (allDay)
            return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? new Stamp(d, TimeZoneInfo.Local, true)
                : null;
        var utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return null;
        var zone = utc ? TimeZoneInfo.Utc : p.Params.TryGetValue("TZID", out var tzid) ? FindZone(tzid) : TimeZoneInfo.Local;
        return new Stamp(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), zone, false);
    }

    private static TimeZoneInfo FindZone(string tzid)
    {
        tzid = tzid.Trim('"');
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzid); // Windows names (Outlook) and IANA ids (Google, iCloud)
        }
        catch (Exception)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static DateTime ToUtc(Stamp s)
    {
        if (s.AllDay) return DateTime.SpecifyKind(s.Wall.Date, DateTimeKind.Utc); // compared as dates
        var wall = s.Zone.IsInvalidTime(s.Wall) ? s.Wall.AddHours(1) : s.Wall; // skipped by a DST jump
        return TimeZoneInfo.ConvertTimeToUtc(wall, s.Zone);
    }

    private static DateTime ToLocal(Stamp s) => TimeZoneInfo.ConvertTimeFromUtc(ToUtc(s), TimeZoneInfo.Local);

    /// <summary>A local (or, with <paramref name="utc"/>, UTC) time as wall-clock time in <paramref name="zone"/>.</summary>
    private static DateTime ToWall(DateTime time, TimeZoneInfo zone, bool utc = false) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(time, utc ? DateTimeKind.Utc : DateTimeKind.Local), zone),
            DateTimeKind.Unspecified);

    // ---------------------------------------------------------------- text

    private static Prop? Get(List<Prop> e, string name) => e.FirstOrDefault(p => p.Name == name);

    private static List<List<Prop>> ReadEvents(string ics)
    {
        var events = new List<List<Prop>>();
        List<Prop>? current = null;
        var depth = 0; // nested blocks inside a VEVENT (VALARM) are skipped
        foreach (var line in Unfold(ics))
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null) depth++;
                else if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) current = new List<Prop>();
                continue;
            }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (current is null) continue;
                if (depth > 0) depth--;
                else
                {
                    events.Add(current);
                    current = null;
                }
                continue;
            }
            if (current is null || depth > 0) continue;
            if (ParseLine(line) is { } prop) current.Add(prop);
        }
        return events;
    }

    /// <summary>Joins folded lines (a continuation starts with a space or tab).</summary>
    private static IEnumerable<string> Unfold(string ics)
    {
        string? pending = null;
        foreach (var raw in ics.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && line[0] is ' ' or '\t' && pending is not null)
            {
                pending += line[1..];
                continue;
            }
            if (pending is not null) yield return pending;
            pending = line;
        }
        if (pending is not null) yield return pending;
    }

    /// <summary>NAME;PARAM=x;PARAM="y:z":value → name, params, value (the first ':' outside quotes splits).</summary>
    private static Prop? ParseLine(string line)
    {
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
                break;
            }
        }
        if (colon <= 0) return null;
        var head = line[..colon].Split(';');
        var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in head.Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) ps[part[..eq]] = part[(eq + 1)..];
        }
        return new Prop(head[0].ToUpperInvariant(), ps, line[(colon + 1)..]);
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();
}
