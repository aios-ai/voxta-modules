using System.Globalization;
using System.Text.RegularExpressions;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Helper;

internal sealed record ForecastWindowRequest(string Start, string End)
{
    private static readonly Regex Endpoint = new(
        @"^(?:today|tomorrow|\d{4}-\d{2}-\d{2})(?:T\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d{2}:\d{2})?)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ForecastWindowRequest? Parse(string? start, string? end)
    {
        if (start == null && end == null) return null;
        if (start == null || end == null)
            throw new ModuleToolInputException("Supply both start and end for a forecast window, or omit both for the usual summary. The end is exclusive.");
        Validate(start);
        Validate(end);
        return new(start, end);
    }

    private static void Validate(string text)
    {
        if (!Endpoint.IsMatch(text))
            throw new ModuleToolInputException("Forecast times must be today/tomorrow, YYYY-MM-DD, or a date with THH:mm[:ss] and optional Z/UTC offset. Examples: tomorrowT06:00, tomorrowT12:00. Without an offset, times are local to the destination.");
        // Validate calendar dates, clock values and offsets before contacting the provider.
        var normalized = Regex.Replace(text, "^(today|tomorrow)", "2000-01-01", RegexOptions.IgnoreCase);
        if (!DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            throw new ModuleToolInputException("Invalid forecast date, time or UTC offset: " + text);
    }

    public ForecastWindow Resolve(GeoResult? location, DateTimeOffset utcNow)
    {
        var zone = LocationTimeZone.Resolve(location);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
        DateTimeOffset ResolveEndpoint(string text)
        {
            var normalized = Regex.Replace(text, "^(today|tomorrow)", m =>
                (m.Value.Equals("tomorrow", StringComparison.OrdinalIgnoreCase) ? today.AddDays(1) : today)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), RegexOptions.IgnoreCase);
            if (Regex.IsMatch(normalized, @"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase))
                return DateTimeOffset.Parse(normalized, CultureInfo.InvariantCulture);
            var local = DateTime.SpecifyKind(DateTime.Parse(normalized, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
                throw new ModuleToolInputException($"{text} is a skipped or ambiguous local time in {zone.Id}. Supply an explicit UTC offset to identify a real instant.");
            return new(local, zone.GetUtcOffset(local));
        }
        var start = ResolveEndpoint(Start);
        var end = ResolveEndpoint(End);
        if (end <= start) throw new ModuleToolInputException("Forecast end must be later than start. For a single hour, request that hour through the next hour.");
        return new(start, end, zone);
    }
}

internal sealed record ForecastWindow(DateTimeOffset Start, DateTimeOffset End, TimeZoneInfo Zone)
{
    public string Format(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, Zone).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
    public bool Contains(long timestamp) => DateTimeOffset.FromUnixTimeSeconds(timestamp) >= Start && DateTimeOffset.FromUnixTimeSeconds(timestamp) < End;

    public IReadOnlyList<T> Select<T>(IEnumerable<T> items, Func<T, long> timestamp)
    {
        var sorted = items.OrderBy(timestamp).ToArray();
        if (sorted.Length == 0) throw new ModuleToolInputException("No forecast entries were returned.");
        var first = DateTimeOffset.FromUnixTimeSeconds(timestamp(sorted[0]));
        var last = DateTimeOffset.FromUnixTimeSeconds(timestamp(sorted[^1]));
        if (End <= first || Start > last)
            throw new ModuleToolInputException($"The requested window has no overlap with the available forecast timestamps: {Format(first)} through {Format(last)} ({Zone.Id}). No forecast for the requested time is available; do not extrapolate these values.");
        // Include one real entry on either side, so a short window between timestamps still has context.
        var inside = sorted.Where(x => Contains(timestamp(x))).ToList();
        var before = Array.FindLastIndex(sorted, x => DateTimeOffset.FromUnixTimeSeconds(timestamp(x)) < Start);
        var after = Array.FindIndex(sorted, x => DateTimeOffset.FromUnixTimeSeconds(timestamp(x)) >= End);
        if (before >= 0) inside.Insert(0, sorted[before]);
        if (after >= 0) inside.Add(sorted[after]);
        return inside;
    }

    public string Describe<T>(IReadOnlyList<T> all, IReadOnlyList<T> selected, Func<T, long> timestamp, int stepHours, bool includeAvailability = true)
    {
        var first = DateTimeOffset.FromUnixTimeSeconds(all.Min(timestamp));
        var last = DateTimeOffset.FromUnixTimeSeconds(all.Max(timestamp));
        var text = $"Requested window: {Format(Start)} to {Format(End)} (end exclusive; {Zone.Id}). ";
        if (includeAvailability)
            text += $"Available forecast timestamps: {Format(first)} through {Format(last)}. Resolution: {stepHours}-hour steps. " +
                "Individual provider entries follow; surrounding context is outside the requested window. Values are not interpolated or day/night averages. ";
        if (Start < first || End > last)
            text += "Partial forecast coverage: requested bounds extend beyond the returned timestamps; only the listed times are available. Do not claim coverage of the whole requested period. ";
        if (!selected.Any(x => Contains(timestamp(x))))
            text += "There is no forecast timestamp inside this narrow window; only the surrounding entries are available. ";
        if (selected.Zip(selected.Skip(1)).Any(pair => timestamp(pair.Second) - timestamp(pair.First) > stepHours * 3600L))
            text += "There are gaps in the returned entries; missing values have not been filled in. ";
        return text;
    }
}
