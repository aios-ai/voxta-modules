using System.Globalization;
using System.Text;
using Voxta.Modules.Aios.OpenWeather.Clients;

namespace Voxta.Modules.Aios.OpenWeather.Helper;

internal static class WeatherReportFormatter
{
    public static string WindUnit(string? units) => units == "imperial" ? "mph" : "m/s";
    private static string FormatWholeNumber(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    public static string Weather(OpenWeatherResponse data, string location, string? units, HashSet<string> details)
    {
        var rain = data.Rain?.OneHour ?? 0;
        var snow = data.Snow?.OneHour ?? 0;
        var unitSuffix = units == "imperial" ? "°F" : "°C";

        var sb = new StringBuilder();

        if (details.Contains("Temp"))
            sb.Append($"The current temperature in {location} ({data.Sys.Country}) is {FormatWholeNumber(data.Main.Temp)}{unitSuffix} ");
        else
            sb.Append($"The current weather in {location} ({data.Sys.Country}) is reported below. ");
        if (details.Contains("FeelsLike"))
            sb.Append($"(feels like {FormatWholeNumber(data.Main.FeelsLike)}{unitSuffix}) ");
        if (details.Contains("Condition"))
            sb.Append($"with {data.Weather.FirstOrDefault()?.Description ?? "unavailable"}. ");
        if (details.Contains("Precipitation"))
            sb.Append($"Rain: {rain:0.#} mm/h; snow: {snow:0.#} mm/h. ");
        if (details.Contains("TempMinMax"))
            sb.Append($"The temperature ranges between {FormatWholeNumber(data.Main.TempMin)}-{FormatWholeNumber(data.Main.TempMax)}{unitSuffix}. ");
        if (details.Contains("Wind"))
            sb.Append($"Wind: {data.Wind.Speed:0.#} {WindUnit(units)} at {FormatWholeNumber(data.Wind.Deg)}°. ");
        if (details.Contains("CloudCover"))
            sb.Append($"Cloud cover: {data.Clouds.All}%. ");
        if (details.Contains("Visibility"))
            sb.Append($"Visibility: {(data.Visibility is { } visibility ? FormatWholeNumber(visibility / 1000.0) + " km" : "unavailable")}. ");

        AppendWeatherExtras(sb, data.Main, details);
        if (details.Contains("WindGust")) sb.Append($"Wind gust: {(data.Wind.Gust is { } gust ? gust.ToString("0.#", CultureInfo.InvariantCulture) + " " + WindUnit(units) : "unavailable")}. ");
        if (details.Contains("Sunrise")) sb.Append($"Sunrise: {LocalTime(data.Sys.Sunrise, data.Timezone)}. ");
        if (details.Contains("Sunset")) sb.Append($"Sunset: {LocalTime(data.Sys.Sunset, data.Timezone)}. ");
        if (details.Contains("Sunrise") || details.Contains("Sunset"))
            sb.Append("These sun times apply only to their displayed date. For tomorrow or another date, use get_sun_times; future times cannot be inferred from this response. ");
        if (details.Contains("ObservationTime")) sb.Append($"Data time: {LocalTime(data.Dt, data.Timezone)}. ");
        if (details.Contains("PrecipitationProbability")) sb.Append("Precipitation probability is only available in the forecast. ");

        return sb.ToString().Trim();
    }

    internal static string LocalTime(long? timestamp, int? offset)
    {
        if (timestamp is not { } value) return "unavailable";
        var time = DateTimeOffset.FromUnixTimeSeconds(value);
        // Without a destination offset, report UTC rather than the host's local time.
        if (offset is { } seconds && Math.Abs((long)seconds) <= 14 * 3600)
            time = time.ToOffset(TimeSpan.FromSeconds(seconds));
        return time.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
    }

    internal static void AppendWeatherExtras(StringBuilder sb, MainInfo main, HashSet<string> details)
    {
        foreach (var (key, label, value, unit) in new[]
        {
            ("Humidity", "Humidity", main.Humidity, "%"),
            ("Pressure", "Pressure", main.Pressure, " hPa"),
            ("SeaLevelPressure", "Sea-level pressure", main.SeaLevelPressure, " hPa"),
            ("GroundPressure", "Ground-level pressure", main.GroundPressure, " hPa"),
        })
            if (details.Contains(key)) sb.Append($"{label}: {(value.HasValue ? value.Value.ToString("0.#", CultureInfo.InvariantCulture) + unit : "unavailable")}. ");
    }

    public static string Pollution(OpenWeatherAirPollutionResponse data, string location, HashSet<string> details, bool includeHeading = true)
    {
        var aqi = data.List[0].Main.Aqi;
        var components = data.List[0].Components;

        var sb = new StringBuilder();
        if (includeHeading) sb.Append($"Current air quality in {location}: ");

        if (details.Contains("AQI"))
        {
            sb.Append($"AQI {aqi} ({AirPollutionForecastSummariser.GetAqiLabel(aqi)}) ");
        }
        if (details.Contains("CO"))
            sb.Append($"CO: {components.Co:0.#} µg/m³, ");
        if (details.Contains("NO"))
            sb.Append($"NO: {components.No:0.#} µg/m³, ");
        if (details.Contains("NO2"))
            sb.Append($"NO₂: {components.No2:0.#} µg/m³, ");
        if (details.Contains("O3"))
            sb.Append($"O₃: {components.O3:0.#} µg/m³, ");
        if (details.Contains("SO2"))
            sb.Append($"SO₂: {components.So2:0.#} µg/m³, ");
        if (details.Contains("PM2.5"))
            sb.Append($"PM2.5: {components.Pm2_5:0.#} µg/m³, ");
        if (details.Contains("PM10"))
            sb.Append($"PM10: {components.Pm10:0.#} µg/m³, ");
        if (details.Contains("NH3"))
            sb.Append($"NH₃: {components.Nh3:0.#} µg/m³, ");

        return sb.ToString().Trim().TrimEnd(',');
    }
}
