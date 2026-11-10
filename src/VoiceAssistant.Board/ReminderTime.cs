using System.Globalization;
using System.Text.RegularExpressions;

namespace VoiceAssistant.Board;

/// <summary>
/// Finds a reminder time in spoken text and removes it from the task: “call mom tomorrow at 5 pm” → “call mom”, tomorrow
/// 17:00. Understands “in 10 minutes / an hour / 2 days”, “in half an hour”, today / tonight / tomorrow / the day after
/// tomorrow, weekdays (“on Monday”, “next Friday”), “at 5”, “at 5:30 pm”, “5 pm”, noon, midnight, and morning /
/// afternoon / evening / night. Without am/pm, the next sensible time is used (“at 5” in the afternoon means 5 pm).
/// </summary>
public static partial class ReminderTime
{
    private static readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
        ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["fifteen"] = 15,
        ["twenty"] = 20, ["thirty"] = 30, ["forty five"] = 45, ["forty-five"] = 45, ["couple of"] = 2, ["few"] = 3,
    };

    public static (string Task, DateTimeOffset? Due) Parse(string text, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var work = AmPmDotsRegex().Replace(text, m => m.Groups[1].Value.ToLowerInvariant() + "m");

        // "in 10 minutes", "after two hours", "in half an hour"
        TimeSpan? offset = null;
        work = Take(work, HalfHourRegex(), _ => offset = TimeSpan.FromMinutes(30));
        work = Take(work, RelativeRegex(), m =>
        {
            var amount = SpacesRegex().Replace(m.Groups["n"].Value.Trim(), " ");
            if (amount.StartsWith("a ", StringComparison.OrdinalIgnoreCase))
            {
                amount = amount[2..]; // "a couple of", "a few"
            }

            var n = int.TryParse(amount, out var d) ? d : Numbers.GetValueOrDefault(amount, 1);
            var unit = m.Groups["u"].Value.ToLowerInvariant();
            offset = unit.StartsWith("min", StringComparison.Ordinal) ? TimeSpan.FromMinutes(n)
                : unit.StartsWith("h", StringComparison.Ordinal) ? TimeSpan.FromHours(n)
                : unit.StartsWith("d", StringComparison.Ordinal) ? TimeSpan.FromDays(n)
                : TimeSpan.FromDays(7 * n);
        });

        if (offset is { } span)
        {
            return (TidyTask(work), now + span);
        }

        int? dayOffset = null;
        int? defaultHour = null;
        string? partOfDay = null;
        work = Take(work, DayRegex(), m =>
        {
            if (m.Groups["part"].Success)
            {
                partOfDay = m.Groups["part"].Value.ToLowerInvariant();
            }

            switch (m.Groups["day"].Value.ToLowerInvariant())
            {
                case "day after tomorrow":
                    dayOffset = 2;
                    break;
                case "tomorrow":
                    dayOffset = 1;
                    break;
                case "tonight":
                    dayOffset = 0;
                    defaultHour = 20;
                    break;
                case "this evening":
                    dayOffset = 0;
                    defaultHour = 18;
                    break;
                case "this afternoon":
                    dayOffset = 0;
                    defaultHour = 15;
                    break;
                case "this morning":
                    dayOffset = 0;
                    defaultHour = 9;
                    break;
                default:
                    dayOffset = 0;
                    break;
            }
        });

        work = Take(work, WeekdayRegex(), m =>
        {
            var target = Enum.Parse<DayOfWeek>(m.Groups["wd"].Value, ignoreCase: true);
            var ahead = ((int)target - (int)now.DayOfWeek + 7) % 7;
            dayOffset = ahead == 0 ? 7 : ahead;
        });

        work = Take(work, PartOfDayRegex(), m => partOfDay = m.Groups["part"].Value.ToLowerInvariant());

        int? hour = null;
        var minute = 0;
        string? ampm = null;
        work = Take(work, NoonRegex(), m =>
        {
            var noon = m.Groups["w"].Value.Equals("noon", StringComparison.OrdinalIgnoreCase);
            hour = noon ? 12 : 0;
            ampm = noon ? "pm" : "am";
        });
        if (hour is null)
        {
            work = Take(work, ClockRegex(), m =>
            {
                hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
                minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
                ampm = m.Groups["ap"].Success ? m.Groups["ap"].Value.ToLowerInvariant() : null;
            });
        }

        var task = TidyTask(work);
        if (dayOffset is null && hour is null && partOfDay is null)
        {
            return (task, null);
        }

        var date = now.Date.AddDays(dayOffset ?? 0);
        if (hour is null)
        {
            var h = partOfDay switch { "morning" => 9, "afternoon" => 15, "evening" => 18, "night" => 21, _ => defaultHour ?? 9 };
            var due = At(zone, now, date, h, 0);
            return (task, dayOffset is null && due <= now ? due.AddDays(1) : due);
        }

        if (hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            return (task, null);
        }

        var h24 = hour.Value;
        if (ampm == "pm" && h24 < 12)
        {
            h24 += 12;
        }
        else if (ampm == "am" && h24 == 12)
        {
            h24 = 0;
        }
        else if (ampm is null && h24 < 12)
        {
            if (partOfDay is "afternoon" or "evening" or "night" || defaultHour >= 15)
            {
                h24 += 12;
            }
            else if (partOfDay is null && dayOffset is null)
            {
                // "at 5": the next 5 o'clock (5 pm in the afternoon, 5 am tomorrow late at night).
                var am = At(zone, now, date, h24, minute);
                var pm = At(zone, now, date, h24 + 12, minute);
                var next = am > now ? am : pm > now ? pm : am.AddDays(1);
                return (task, next);
            }
            else if (partOfDay is null && h24 < 7)
            {
                h24 += 12; // "tomorrow at 5" is 5 pm, not 5 am
            }
        }

        var result = At(zone, now, date, h24, minute);
        return (task, dayOffset is null && result <= now ? result.AddDays(1) : result);
    }

    /// <summary>
    /// A wall-clock time on a date. In the PC's own time zone the offset is the one in force on that date, so a reminder
    /// past a daylight-saving change still fires at the time said. A <paramref name="now"/> in another zone keeps its
    /// fixed offset.
    /// </summary>
    private static DateTimeOffset At(TimeZoneInfo? zone, DateTimeOffset now, DateTime date, int hour, int minute)
    {
        var local = new DateTime(date.Year, date.Month, date.Day, hour, minute, 0, DateTimeKind.Unspecified);
        zone ??= TimeZoneInfo.Local;
        if (zone.GetUtcOffset(now) != now.Offset)
        {
            return new DateTimeOffset(local, now.Offset);
        }

        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1); // skipped by the spring change: the first moment that exists
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    private static string Take(string text, Regex regex, Action<Match> found)
    {
        var m = regex.Match(text);
        if (!m.Success)
        {
            return text;
        }

        found(m);
        return text.Remove(m.Index, m.Length).Insert(m.Index, " ");
    }

    private static string TidyTask(string text)
    {
        var t = MultiSpaceRegex().Replace(text, " ").Trim().Trim(',', ';', ':', '.', '!', ' ');
        t = TrailingLinkRegex().Replace(t, string.Empty).Trim().Trim(',', ';', ':', '.', ' ');
        return t;
    }

    [GeneratedRegex(@"\b([ap])\.\s?m\.?", RegexOptions.IgnoreCase)]
    private static partial Regex AmPmDotsRegex();

    [GeneratedRegex(@"\b(?:in|after)\s+half\s+an?\s+hour\b", RegexOptions.IgnoreCase)]
    private static partial Regex HalfHourRegex();

    [GeneratedRegex(@"\b(?:in|after)\s+(?<n>\d+|an?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty[\s-]five|a\s+couple\s+of|couple\s+of|a\s+few|few)\s+(?<u>minutes?|mins?|hours?|hrs?|days?|weeks?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RelativeRegex();

    [GeneratedRegex(@"\b(?<day>day\s+after\s+tomorrow|tomorrow|today|tonight|this\s+evening|this\s+afternoon|this\s+morning)(?:\s+(?<part>morning|afternoon|evening|night))?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayRegex();

    [GeneratedRegex(@"\b(?:on\s+|this\s+|next\s+|coming\s+)?(?<wd>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayRegex();

    [GeneratedRegex(@"\b(?:in\s+the\s+|at\s+)(?<part>morning|afternoon|evening|night)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PartOfDayRegex();

    [GeneratedRegex(@"\b(?:at\s+)?(?<w>noon|midnight)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoonRegex();

    // "at 5", "at 5:30", "5 pm", "5:30 pm", "17:00" (a bare number needs "at" or am/pm, so "call 3 people" stays).
    [GeneratedRegex(@"\b(?:at\s+(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?(?:\s*(?<ap>am|pm))?|(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<ap>am|pm)|(?<h>\d{1,2}):(?<m>\d{2}))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\b(?:at|on|by|in|for|from)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingLinkRegex();
}
