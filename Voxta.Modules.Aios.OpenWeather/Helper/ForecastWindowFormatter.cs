using System.Globalization;
using System.Text;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Helper;

internal static class ForecastWindowFormatter
{
    internal static long Timestamp(ForecastItem item) => item.Dt != 0 ? item.Dt :
        new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(item.DtTxt!, CultureInfo.InvariantCulture), DateTimeKind.Utc)).ToUnixTimeSeconds();

    internal static string Availability<T>(IReadOnlyList<T> all, Func<T, long> timestamp, TimeZoneInfo zone, int stepHours)
    {
        string Local(long value) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(value), zone)
            .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
        return $"Available forecast timestamps: {Local(all.Min(timestamp))} through {Local(all.Max(timestamp))} ({zone.Id}). Resolution: {stepHours}-hour steps.";
    }

    public static string Weather(OpenWeatherForecastResponse data, ForecastWindow window, string location, string? units, HashSet<string> details)
        => Weather(data, new ForecastSelection([window]), location, units, details);

    public static string Weather(OpenWeatherForecastResponse data, ForecastSelection selection, string location, string? units, HashSet<string> details)
        => Format(data.List, Timestamp, selection, $"Weather forecast for {location}", 3,
            details.Contains("Precipitation") ? "Rain/snow volumes are the provider's three-hour accumulations ending at each labelled valid time, not totals for the requested window." : null,
            item => WeatherValues(item, units, details));

    private static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
    private static string WeatherValues(ForecastItem item, string? units, HashSet<string> details)
    {
        var sb = new StringBuilder();
        var temperatureUnit = units == "imperial" ? "°F" : "°C";
        if (details.Contains("Temp")) sb.Append($"Temperature: {Number(item.Main.Temp)}{temperatureUnit}. ");
        if (details.Contains("FeelsLike")) sb.Append($"Feels like: {Number(item.Main.FeelsLike)}{temperatureUnit}. ");
        if (details.Contains("TempMinMax")) sb.Append($"Temperature bounds at this valid time (not daily low/high): {Number(item.Main.TempMin)} to {Number(item.Main.TempMax)}{temperatureUnit}. ");
        if (details.Contains("Condition")) sb.Append($"Condition: {item.Weather.FirstOrDefault()?.Description ?? "unavailable"}. ");
        if (details.Contains("Wind")) sb.Append($"Wind: {Number(item.Wind.Speed)} {WeatherReportFormatter.WindUnit(units)} at {item.Wind.Deg}°. ");
        if (details.Contains("WindGust")) sb.Append($"Wind gust: {(item.Wind.Gust is { } gust ? Number(gust) + " " + WeatherReportFormatter.WindUnit(units) : "unavailable")}. ");
        if (details.Contains("CloudCover")) sb.Append($"Cloud cover: {item.Clouds.All}%. ");
        if (details.Contains("Visibility")) sb.Append($"Visibility: {(item.Visibility is { } visibility ? Number(visibility / 1000.0) + " km" : "unavailable")}. ");
        if (details.Contains("Precipitation")) sb.Append($"Rain: {Number(item.Rain?.ThreeHours ?? 0)} mm/3h; snow: {Number(item.Snow?.ThreeHours ?? 0)} mm/3h. ");
        if (details.Contains("PrecipitationProbability")) sb.Append($"Precipitation probability: {(item.Pop is { } pop ? Number(pop * 100) + "%" : "unavailable")}. ");
        WeatherReportFormatter.AppendWeatherExtras(sb, item.Main, details);
        if (details.Contains("ObservationTime")) sb.Append("The labelled time is forecast validity, not an observation time. ");
        return sb.ToString().Trim();
    }

    public static string Pollution(OpenWeatherAirPollutionResponse data, ForecastWindow window, string location, HashSet<string> details)
        => Pollution(data, new ForecastSelection([window]), location, details);

    public static string Pollution(OpenWeatherAirPollutionResponse data, ForecastSelection selection, string location, HashSet<string> details)
        => Format(data.List, item => item.Dt, selection, $"Air pollution forecast for {location}", 1, null,
            item => WeatherReportFormatter.Pollution(new() { List = [item] }, location, details, includeHeading: false));

    private static string Format<T>(IReadOnlyList<T> all, Func<T, long> timestamp, ForecastSelection selection,
        string heading, int stepHours, string? note, Func<T, string> values)
    {
        if (all.Count == 0) throw new ModuleToolInputException("No forecast entries were returned.");
        var first = DateTimeOffset.FromUnixTimeSeconds(all.Min(timestamp));
        var last = DateTimeOffset.FromUnixTimeSeconds(all.Max(timestamp));
        var reference = selection.Windows[0];
        bool Overlaps(ForecastWindow window) => window.End > first && window.Start <= last;
        var availability = Availability(all, timestamp, reference.Zone, stepHours);
        if (!selection.Windows.Any(Overlaps))
        {
            var requested = string.Join("; ", selection.Windows.Select(w => $"{w.Format(w.Start)} to {w.Format(w.End)}"));
            throw new ModuleToolInputException($"No forecast coverage for the requested period(s): {requested} (end exclusive). {availability} The request has no overlap with the available forecast timestamps. This endpoint cannot supply the requested dates; tell the user the limit. Do not extrapolate, substitute other dates, or retry different arguments to extend the horizon.");
        }
        var sb = new StringBuilder(heading + ":\n");
        sb.AppendLine(availability);
        if (selection.Windows.Any(w => !Overlaps(w) || w.Start < first || w.End > last))
            sb.AppendLine("Partial forecast coverage: the requested selection is not fully covered by the available timestamps. Report the missing periods; do not describe the whole selection as available or extrapolate beyond the returned data.");
        sb.AppendLine("Individual provider entries follow. Surrounding context is outside each requested window; values are not interpolated or day/night averages.");
        if (selection.Windows.Count > 1) sb.AppendLine("Periods are separate; intervening hours are not part of the selection.");
        if (selection.ElapsedPeriods > 0) sb.AppendLine($"Elapsed selected periods omitted: {selection.ElapsedPeriods}; forecasts describe remaining time only.");
        if (note != null) sb.AppendLine(note);
        foreach (var window in selection.Windows)
        {
            if (!Overlaps(window))
            {
                sb.AppendLine($"Requested window: {window.Format(window.Start)} to {window.Format(window.End)} (end exclusive; {window.Zone.Id}). No forecast coverage for this period; no substitute samples supplied.");
                continue;
            }
            var selected = window.Select(all, timestamp);
            sb.AppendLine(window.Describe(all, selected, timestamp, stepHours, includeAvailability: false));
            foreach (var item in selected)
            {
                var value = values(item);
                if (value.Length == 0) continue;
                sb.Append(window.Format(DateTimeOffset.FromUnixTimeSeconds(timestamp(item))));
                if (!window.Contains(timestamp(item))) sb.Append(" [surrounding context]");
                sb.Append(": ");
                sb.AppendLine(value);
            }
        }
        return sb.ToString().Trim();
    }
}
