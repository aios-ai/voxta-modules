using System.Globalization;
using System.Text.RegularExpressions;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Helper;

// Controlled English is an input notation, not a natural-language interpretation service.
// Every accepted expression compiles to the same calendar/elapsed-time rules used by forecast selection.
internal sealed record WeatherWhen(bool Current, ForecastSelectionRequest? Selection = null)
{
    private const string Day = @"(?:today|tomorrow|day after tomorrow|(?:next )?(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)|[0-9]{4}-[0-9]{2}-[0-9]{2})";
    private const string Period = @"(?:morning|afternoon|evening|night|all day|[0-9]{2}:[0-9]{2}-[0-9]{2}:[0-9]{2})";

    public static WeatherWhen Parse(string? when)
    {
        if (when == null) return new(true);
        var text = Regex.Replace(when.Trim().ToLowerInvariant().Replace('_', ' '), @"\s+", " ");
        text = text.Replace("the day after tomorrow", "day after tomorrow", StringComparison.Ordinal);
        if (text == "now") return new(true);
        if (text is "forecast" or "next few days") return new(false);
        if (text is "rest of today" or "rest of the day") text = "today";
        if (text is "tomorrow and day after tomorrow" or "tomorrow and the day after tomorrow")
            return Select("tomorrow", range: "days", count: 2);

        // Exact continuous intervals remain available without extra public arguments.
        var interval = Regex.Match(text, @"^(?<start>[^ /]+t[^ /]+)(?:/| to )(?<end>[^ /]+t[^ /]+)$");
        if (interval.Success)
            return new(false, ForecastSelectionRequest.Parse(interval.Groups["start"].Value, interval.Groups["end"].Value));

        var dates = Regex.Match(text, @"^(?<first>[0-9]{4}-[0-9]{2}-[0-9]{2}) to (?<last>[0-9]{4}-[0-9]{2}-[0-9]{2})(?: (?<period>" + Period + "))?$");
        if (dates.Success)
        {
            if (!DateOnly.TryParseExact(dates.Groups["first"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
                || !DateOnly.TryParseExact(dates.Groups["last"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)
                || first.Year is < 1901 or > 2099 || last.Year is < 1901 or > 2099
                || last < first || last.DayNumber - first.DayNumber >= 31)
                throw new ModuleToolInputException("when date ranges require valid dates in ascending order, at most 31 inclusive calendar days.");
            return Select(dates.Groups["first"].Value, Optional(dates, "period"), "days", last.DayNumber - first.DayNumber + 1);
        }
        var next = Regex.Match(text, @"^next (?<count>[0-9]+) (?<unit>hours?|days?|mornings?|afternoons?|evenings?|nights?)(?: (?<period>" + Period + "))?$");
        if (next.Success)
        {
            var unit = next.Groups["unit"].Value.TrimEnd('s');
            var period = Optional(next, "period");
            if (unit is not ("hour" or "day"))
            {
                if (period != null) throw Unsupported();
                period = unit;
            }
            return Select(period: period, range: unit == "hour" ? "hours" : "days", count: Count(next));
        }
        var from = Regex.Match(text, @"^(?<count>[0-9]+) days? from (?<date>" + Day + @")(?: (?<period>" + Period + "))?$");
        if (from.Success) return Select(from.Groups["date"].Value, Optional(from, "period"), "days", Count(from));
        var calendar = Regex.Match(text, @"^(?<range>(?:this |next )?weekend|this week|next week)(?: (?<period>" + Period + "))?$");
        if (calendar.Success)
            return Select(period: Optional(calendar, "period"), range: calendar.Groups["range"].Value.Replace("this weekend", "weekend").Replace(' ', '_'));
        var day = Regex.Match(text, @"^(?<date>" + Day + @")(?: (?<period>" + Period + "))?$");
        if (day.Success) return Select(day.Groups["date"].Value, Optional(day, "period"));
        if (Regex.IsMatch(text, "^" + Period + "$")) return Select(period: text);
        throw Unsupported();
    }

    private static WeatherWhen Select(string? date = null, string? period = null, string? range = null, int? count = null)
        => new(false, ForecastSelectionRequest.Parse(null, null, date?.Replace(' ', '_'), period?.Replace(' ', '_'), range, count));

    private static string? Optional(Match match, string group) => match.Groups[group].Success ? match.Groups[group].Value : null;
    private static int Count(Match match) => int.TryParse(match.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
        ? count : throw new ModuleToolInputException("when count is too large; use next 1-744 hours or next 1-31 days.");
    private static ModuleToolInputException Unsupported() => new("Unsupported when. Use now, tomorrow morning, next 5 hours, next 3 days, this weekend, YYYY-MM-DD, or YYYY-MM-DD to YYYY-MM-DD. Periods can be morning, afternoon, evening, night or HH:mm-HH:mm.");
}
