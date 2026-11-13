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
    {
        var temperatureUnit = units == "imperial" ? "°F" : "°C";
        var windUnit = WeatherReportFormatter.WindUnit(units);
        (string Key, string Label, Func<ForecastItem, string> Value)[] fields =
        [
            ("Temp", $"Temperature ({temperatureUnit})", item => Number(item.Main.Temp)),
            ("FeelsLike", $"Feels like ({temperatureUnit})", item => Number(item.Main.FeelsLike)),
            ("TempMinMax", $"Temperature lower bound ({temperatureUnit})", item => Number(item.Main.TempMin)),
            ("TempMinMax", $"Temperature upper bound ({temperatureUnit})", item => Number(item.Main.TempMax)),
            ("Condition", "Condition", item => item.Weather.FirstOrDefault()?.Description ?? "unavailable"),
            ("Wind", $"Wind speed ({windUnit})", item => Number(item.Wind.Speed)),
            ("Wind", "Wind direction (°)", item => Number(item.Wind.Deg)),
            ("WindGust", $"Wind gust ({windUnit})", item => Number(item.Wind.Gust)),
            ("CloudCover", "Cloud cover (%)", item => Number(item.Clouds.All)),
            ("Visibility", "Visibility (km)", item => Number(item.Visibility / 1000.0)),
            ("Precipitation", "Rain (mm/3h)", item => WeatherReportFormatter.PrecipitationAmount(item.Rain?.ThreeHours ?? 0)),
            ("Precipitation", "Snow (mm/3h)", item => WeatherReportFormatter.PrecipitationAmount(item.Snow?.ThreeHours ?? 0)),
            ("PrecipitationProbability", "Precipitation probability (%)", item => item.Pop is { } pop
                ? WeatherReportFormatter.PrecipitationAmount(pop * 100) : "unavailable"),
            ("Humidity", "Humidity (%)", item => Number(item.Main.Humidity)),
            ("Pressure", "Pressure (hPa)", item => Number(item.Main.Pressure)),
            ("SeaLevelPressure", "Sea-level pressure (hPa)", item => Number(item.Main.SeaLevelPressure)),
            ("GroundPressure", "Ground-level pressure (hPa)", item => Number(item.Main.GroundPressure)),
        ];
        var columns = fields.Where(field => details.Contains(field.Key)).ToArray();
        var notes = new List<string>();
        if (details.Contains("Precipitation"))
            notes.Add("Rain and snow are three-hour accumulations ending at each valid time.");
        if (details.Contains("TempMinMax"))
            notes.Add("Temperature bounds apply to each sample, rather than the whole day.");
        if (details.Contains("ObservationTime"))
            notes.Add("Timestamps show forecast valid times; observation times are unavailable.");

        var header = string.Join(" | ", new[] { "Local valid time" }.Concat(columns.Select(column => column.Label)));
        return Format(data.List, Timestamp, selection,
            $"Weather forecast for {location}", 3, notes.Count > 0 ? string.Join("\n", notes) : null,
            item => string.Join(" | ", columns.Select(column => TableCell(column.Value(item)))), header,
            columns.Length > 0 || details.Contains("ObservationTime"));
    }

    private static IReadOnlyList<ForecastWindow> MergeAdjacent(IReadOnlyList<ForecastWindow> windows)
    {
        var merged = new List<ForecastWindow>();
        foreach (var window in windows)
        {
            if (merged.Count > 0 && merged[^1].End == window.Start && merged[^1].Zone.Equals(window.Zone))
                merged[^1] = merged[^1] with { End = window.End };
            else
                merged.Add(window);
        }
        return merged;
    }

    private static string Number(double? value) => value?.ToString("0.#", CultureInfo.InvariantCulture) ?? "unavailable";
    private static string TableCell(string value) => value.Replace('|', '¦').Replace('\r', ' ').Replace('\n', ' ');

    public static string Pollution(OpenWeatherAirPollutionResponse data, ForecastWindow window, string location, HashSet<string> details)
        => Pollution(data, new ForecastSelection([window]), location, details);

    public static string Pollution(OpenWeatherAirPollutionResponse data, ForecastSelection selection, string location, HashSet<string> details)
        => Format(data.List, item => item.Dt, selection, $"Air pollution forecast for {location}", 1, null,
            item => WeatherReportFormatter.Pollution(new() { List = [item] }, location, details, includeHeading: false));

    private static string Format<T>(IReadOnlyList<T> all, Func<T, long> timestamp, ForecastSelection selection,
        string heading, int stepHours, string? note, Func<T, string> values, string? header = null, bool showRows = true)
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
        var windows = header == null ? selection.Windows : MergeAdjacent(selection.Windows);
        var sb = new StringBuilder(heading + ":\n");
        sb.AppendLine(availability);
        if (windows.Any(w => !Overlaps(w) || w.Start < first || w.End > last))
        {
            if (header == null)
                sb.AppendLine("Partial forecast coverage: the requested selection is not fully covered by the available timestamps. Report the missing periods; do not describe the whole selection as available or extrapolate beyond the returned data.");
            else
            {
                sb.AppendLine("Partial forecast coverage. Report the unavailable portions below and limit forecast claims to returned samples.");
                foreach (var window in windows)
                {
                    if (!Overlaps(window))
                        sb.AppendLine($"Unavailable: {window.Format(window.Start)} to {window.Format(window.End)} (end exclusive).");
                    else
                    {
                        if (window.Start < first)
                            sb.AppendLine($"Unavailable: {window.Format(window.Start)} until the first sample at {window.Format(first)}.");
                        if (window.End > last)
                            sb.AppendLine($"Unavailable: after the last sample at {window.Format(last)} until {window.Format(window.End)}.");
                    }
                }
            }
        }
        sb.AppendLine("Individual provider entries follow. Surrounding context is outside each requested window; values are not interpolated or day/night averages.");
        if (windows.Count > 1) sb.AppendLine("Periods are separate; intervening hours are not part of the selection.");
        if (selection.ElapsedPeriods > 0) sb.AppendLine($"Elapsed selected periods omitted: {selection.ElapsedPeriods}; forecasts describe remaining time only.");
        if (note != null) sb.AppendLine(note);
        foreach (var window in windows)
        {
            if (!Overlaps(window))
            {
                sb.AppendLine($"Requested window: {window.Format(window.Start)} to {window.Format(window.End)} (end exclusive; {window.Zone.Id}). No forecast coverage for this period; no substitute samples supplied.");
                continue;
            }
            var selected = window.Select(all, timestamp);
            sb.AppendLine(window.Describe(all, selected, timestamp, stepHours, includeAvailability: false, includeCoverageWarning: header == null).TrimEnd());
            if (!showRows) continue;
            if (header != null) sb.AppendLine(header);
            foreach (var item in selected)
            {
                var value = values(item);
                if (value.Length == 0 && header == null) continue;
                sb.Append(window.Format(DateTimeOffset.FromUnixTimeSeconds(timestamp(item))));
                if (!window.Contains(timestamp(item))) sb.Append(" [surrounding context]");
                if (value.Length > 0) sb.Append(header == null ? ": " : " | ");
                sb.AppendLine(value);
            }
        }
        return sb.ToString().Trim();
    }
}
