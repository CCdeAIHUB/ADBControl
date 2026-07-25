using System.Globalization;
using ADBControl.Desktop.Models;

namespace ADBControl.Desktop.Services.Automation;

public static class AutomationScheduleCalculator
{
    public static DateTimeOffset? GetNextOccurrence(
        AutomationTaskDefinition task,
        AutomationTriggerDefinition trigger,
        DateTimeOffset afterExclusive)
    {
        return trigger.Type.ToLowerInvariant() switch
        {
            "once" => trigger.RunAt is { } runAt && runAt > afterExclusive ? runAt : null,
            "daily" => NextDaily(trigger, afterExclusive),
            "weekly" => NextWeekly(trigger, afterExclusive),
            "interval" => NextInterval(task.CreatedAt, trigger.IntervalSeconds, afterExclusive),
            "cron" => NextCron(trigger, afterExclusive),
            _ => null,
        };
    }

    private static DateTimeOffset NextDaily(AutomationTriggerDefinition trigger, DateTimeOffset afterExclusive)
    {
        var zone = ResolveTimeZone(trigger.TimeZoneId);
        var time = ParseTime(trigger.At);
        var localAfter = TimeZoneInfo.ConvertTime(afterExclusive, zone);
        var date = localAfter.Date;
        for (var offset = 0; offset <= 370; offset++)
        {
            var candidate = ToInstant(date.AddDays(offset).Add(time.ToTimeSpan()), zone);
            if (candidate > afterExclusive)
                return candidate;
        }
        throw new AutomationExecutionException("SCHEDULE_RANGE_EXCEEDED", "无法计算每天触发器的下一次时间。", "automation.schedule");
    }

    private static DateTimeOffset NextWeekly(AutomationTriggerDefinition trigger, DateTimeOffset afterExclusive)
    {
        var zone = ResolveTimeZone(trigger.TimeZoneId);
        var time = ParseTime(trigger.At);
        var days = trigger.Days.ToHashSet();
        var localAfter = TimeZoneInfo.ConvertTime(afterExclusive, zone);
        for (var offset = 0; offset <= 14; offset++)
        {
            var date = localAfter.Date.AddDays(offset);
            if (!days.Contains(date.DayOfWeek))
                continue;
            var candidate = ToInstant(date.Add(time.ToTimeSpan()), zone);
            if (candidate > afterExclusive)
                return candidate;
        }
        throw new AutomationExecutionException("SCHEDULE_RANGE_EXCEEDED", "无法计算每周触发器的下一次时间。", "automation.schedule");
    }

    private static DateTimeOffset NextInterval(DateTimeOffset anchor, int intervalSeconds, DateTimeOffset afterExclusive)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        if (afterExclusive < anchor)
            return anchor + interval;
        var elapsedTicks = (afterExclusive - anchor).Ticks;
        var intervals = elapsedTicks / interval.Ticks + 1;
        return anchor + TimeSpan.FromTicks(intervals * interval.Ticks);
    }

    private static DateTimeOffset NextCron(AutomationTriggerDefinition trigger, DateTimeOffset afterExclusive)
    {
        if (!AutomationCronExpression.TryParse(trigger.Cron ?? string.Empty, out var expression, out var error))
            throw new AutomationExecutionException("SCHEDULE_CRON_INVALID", error, "automation.schedule");
        return expression!.GetNextOccurrence(afterExclusive, ResolveTimeZone(trigger.TimeZoneId));
    }

    internal static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Local;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new AutomationExecutionException("SCHEDULE_TIME_ZONE_INVALID", $"找不到有效时区：{id}。", "automation.schedule", innerException: ex);
        }
    }

    private static TimeOnly ParseTime(string? value)
    {
        if (!TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new AutomationExecutionException("SCHEDULE_TIME_INVALID", $"无效的时间：{value}。", "automation.schedule");
        return result;
    }

    internal static DateTimeOffset ToInstant(DateTime localDateTime, TimeZoneInfo zone)
    {
        localDateTime = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(localDateTime))
            localDateTime = localDateTime.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(localDateTime)
            ? zone.GetAmbiguousTimeOffsets(localDateTime).Max()
            : zone.GetUtcOffset(localDateTime);
        return new DateTimeOffset(localDateTime, offset);
    }
}

internal sealed class AutomationCronExpression
{
    private readonly CronField _minute;
    private readonly CronField _hour;
    private readonly CronField _day;
    private readonly CronField _month;
    private readonly CronField _dayOfWeek;

    private AutomationCronExpression(CronField minute, CronField hour, CronField day, CronField month, CronField dayOfWeek)
    {
        _minute = minute;
        _hour = hour;
        _day = day;
        _month = month;
        _dayOfWeek = dayOfWeek;
    }

    public static bool TryParse(string value, out AutomationCronExpression? expression, out string error)
    {
        expression = null;
        error = string.Empty;
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            error = "Cron 必须包含 minute hour day month day-of-week 五段。";
            return false;
        }

        if (!CronField.TryParse(fields[0], 0, 59, false, out var minute, out error) ||
            !CronField.TryParse(fields[1], 0, 23, false, out var hour, out error) ||
            !CronField.TryParse(fields[2], 1, 31, false, out var day, out error) ||
            !CronField.TryParse(fields[3], 1, 12, false, out var month, out error) ||
            !CronField.TryParse(fields[4], 0, 7, true, out var dayOfWeek, out error))
            return false;

        expression = new AutomationCronExpression(minute!, hour!, day!, month!, dayOfWeek!);
        return true;
    }

    public DateTimeOffset GetNextOccurrence(DateTimeOffset afterExclusive, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(afterExclusive, zone).DateTime;
        var candidate = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        const int maximumMinutes = 60 * 24 * 366 * 3;
        for (var index = 0; index < maximumMinutes; index++, candidate = candidate.AddMinutes(1))
        {
            if (!_minute.Contains(candidate.Minute) || !_hour.Contains(candidate.Hour) || !_month.Contains(candidate.Month))
                continue;

            var dayMatches = _day.Contains(candidate.Day);
            var weekdayMatches = _dayOfWeek.Contains((int)candidate.DayOfWeek);
            var calendarMatches = _day.IsWildcard && _dayOfWeek.IsWildcard
                ? true
                : _day.IsWildcard
                    ? weekdayMatches
                    : _dayOfWeek.IsWildcard
                        ? dayMatches
                        : dayMatches || weekdayMatches;
            if (!calendarMatches)
                continue;

            var instant = AutomationScheduleCalculator.ToInstant(candidate, zone);
            if (instant > afterExclusive)
                return instant;
        }
        throw new AutomationExecutionException("SCHEDULE_RANGE_EXCEEDED", "三年范围内没有找到下一次 Cron 执行时间。", "automation.schedule");
    }

    private sealed class CronField
    {
        private readonly HashSet<int> _values;

        private CronField(HashSet<int> values, bool isWildcard)
        {
            _values = values;
            IsWildcard = isWildcard;
        }

        public bool IsWildcard { get; }
        public bool Contains(int value) => _values.Contains(value);

        public static bool TryParse(
            string text,
            int minimum,
            int maximum,
            bool normalizeSunday,
            out CronField? field,
            out string error)
        {
            field = null;
            error = string.Empty;
            var values = new HashSet<int>();
            var wildcard = text == "*";
            foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var rangeAndStep = item.Split('/', StringSplitOptions.TrimEntries);
                if (rangeAndStep.Length > 2 || (rangeAndStep.Length == 2 && (!int.TryParse(rangeAndStep[1], out var step) || step < 1)))
                {
                    error = $"Cron 字段步长无效：{item}。";
                    return false;
                }
                step = rangeAndStep.Length == 2 ? int.Parse(rangeAndStep[1], CultureInfo.InvariantCulture) : 1;
                var range = rangeAndStep[0];
                int start;
                int end;
                if (range == "*")
                {
                    start = minimum;
                    end = maximum;
                }
                else if (range.Contains('-', StringComparison.Ordinal))
                {
                    var bounds = range.Split('-', StringSplitOptions.TrimEntries);
                    if (bounds.Length != 2 || !int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end))
                    {
                        error = $"Cron 范围无效：{item}。";
                        return false;
                    }
                }
                else if (int.TryParse(range, out start))
                {
                    end = start;
                }
                else
                {
                    error = $"Cron 值无效：{item}。";
                    return false;
                }

                if (start < minimum || end > maximum || start > end)
                {
                    error = $"Cron 值超出 {minimum}-{maximum}：{item}。";
                    return false;
                }

                for (var value = start; value <= end; value += step)
                    values.Add(normalizeSunday && value == 7 ? 0 : value);
            }

            if (values.Count == 0)
            {
                error = "Cron 字段不能为空。";
                return false;
            }
            field = new CronField(values, wildcard);
            return true;
        }
    }
}
