namespace IntoChat.OS.Modules.Apps.AppRuns;

// An app schedule's cadence, as [Every] takes it: "@minutely", "@hourly", "@daily", "@weekly", a duration ("30s", "5m", "2h", "1d"), or a
// five-field cron ("*/15 9-17 * * 1-5"). A duration or shorthand ticks on a fixed period; a cron is checked once a minute.
internal sealed record Cadence(TimeSpan Period, string[]? Cron)
{
    // Reminders tick no faster than this.
    internal static readonly TimeSpan Shortest = TimeSpan.FromMinutes(1);

    internal static Cadence? Parse(string expression)
    {
        var text = expression?.Trim() ?? "";
        switch (text)
        {
            case "@minutely": return new(TimeSpan.FromMinutes(1), null);
            case "@hourly": return new(TimeSpan.FromHours(1), null);
            case "@daily" or "@midnight": return new(TimeSpan.FromDays(1), null);
            case "@weekly": return new(TimeSpan.FromDays(7), null);
        }
        if (text.Length > 1 && int.TryParse(text[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var amount) && amount > 0)
        {
            TimeSpan? period = text[^1] switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => null,
            };
            return period is { } value ? new(value < Shortest ? Shortest : value, null) : null;
        }
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 5 && Ranges.Select((range, i) => Matches(fields[i], range.Min, range.Min, range.Max) is not null).All(ok => ok)
            ? new(Shortest, fields)
            : null;
    }

    // Whether a tick at this minute is due: always for a period, on matching minutes for a cron.
    internal bool IsDue(DateTimeOffset at)
    {
        if (Cron is null) { return true; }
        int[] values = [at.Minute, at.Hour, at.Day, at.Month, (int)at.DayOfWeek];
        return Cron.Select((field, i) => Matches(field, values[i], Ranges[i].Min, Ranges[i].Max) == true).All(ok => ok);
    }

    private static readonly (int Min, int Max)[] Ranges = [(0, 59), (0, 23), (1, 31), (1, 12), (0, 6)];

    // True or false for a valid field, null when the field cannot be read.
    private static bool? Matches(string field, int value, int min, int max)
    {
        var any = false;
        foreach (var part in field.Split(','))
        {
            var (range, step) = part.Split('/') switch
            {
                [var r] => (r, 1),
                [var r, var s] when int.TryParse(s, out var n) && n > 0 => (r, n),
                _ => (null, 0),
            };
            if (range is null) { return null; }
            int low, high;
            if (range == "*") { (low, high) = (min, max); }
            else if (range.Split('-') is [var a, var b] && int.TryParse(a, out low) && int.TryParse(b, out high)) { }
            else if (int.TryParse(range, out low)) { high = step > 1 ? max : low; }
            else { return null; }
            if (low < min || high > max || low > high) { return null; }
            if (value >= low && value <= high && (value - low) % step == 0) { any = true; }
        }
        return any;
    }
}
