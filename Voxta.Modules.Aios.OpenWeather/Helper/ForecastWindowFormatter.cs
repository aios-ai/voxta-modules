using System.Globalization;
using System.Text;
using Voxta.Modules.Aios.OpenWeather.Clients;

namespace Voxta.Modules.Aios.OpenWeather.Helper;

internal static class ForecastWindowFormatter
{
    internal static long Timestamp(ForecastItem item) => item.Dt != 0 ? item.Dt :
        new DateTimeOffset(DateTime.SpecifyKind(DateTime.Parse(item.DtTxt!, CultureInfo.InvariantCulture), DateTimeKind.Utc)).ToUnixTimeSeconds();

    public static string Weather(OpenWeatherForecastResponse data, ForecastWindow window, string location, string? units, HashSet<string> details)
    {
        var selected = window.Select(data.List, Timestamp);
        var sb = new StringBuilder($"Weather forecast for {location}:\n");
        sb.AppendLine(window.Describe(data.List, selected, Timestamp, 3));
        if (details.Contains("Precipitation")) sb.AppendLine("Rain/snow volumes are the provider's three-hour accumulations ending at each labelled valid time, not totals for the requested window.");
        foreach (var item in selected)
        {
            sb.Append(window.Format(DateTimeOffset.FromUnixTimeSeconds(Timestamp(item))));
            if (!window.Contains(Timestamp(item))) sb.Append(" [surrounding context]");
            sb.Append(": ");
            sb.AppendLine(WeatherValues(item, units, details));
        }
        return sb.ToString().Trim();
    }

    private static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
    private static string WeatherValues(ForecastItem item, string? units, HashSet<string> details)
    {
        var sb = new StringBuilder();
        var temperatureUnit = units == "imperial" ? "°F" : "°C";
        if (details.Contains("Temp")) sb.Append($"Temperature: {Number(item.Main.Temp)}{temperatureUnit}. ");
        if (details.Contains("FeelsLike")) sb.Append($"Feels like: {Number(item.Main.FeelsLike)}{temperatureUnit}. ");
        if (details.Contains("TempMinMax")) sb.Append($"Temperature bounds at this valid time: {Number(item.Main.TempMin)} to {Number(item.Main.TempMax)}{temperatureUnit}. ");
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
    {
        var selected = window.Select(data.List, x => x.Dt);
        var sb = new StringBuilder($"Air pollution forecast for {location}:\n");
        sb.AppendLine(window.Describe(data.List, selected, x => x.Dt, 1));
        foreach (var item in selected)
        {
            sb.Append(window.Format(DateTimeOffset.FromUnixTimeSeconds(item.Dt)));
            if (!window.Contains(item.Dt)) sb.Append(" [surrounding context]");
            sb.Append(": ");
            sb.AppendLine(WeatherReportFormatter.Pollution(new() { List = [item] }, location, details, includeHeading: false));
        }
        return sb.ToString().Trim();
    }
}
