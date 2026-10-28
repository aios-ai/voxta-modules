using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.OpenWeather.Tools;

internal static class WeatherDetailSelection
{
    public const string Weather = "Temp,FeelsLike,Condition,Precipitation,TempMinMax,Wind,WindGust,CloudCover,Visibility,Humidity,Pressure,SeaLevelPressure,GroundPressure,Sunrise,Sunset,ObservationTime";
    public const string Forecast = Weather + ",PrecipitationProbability";
    public const string Pollution = "AQI,CO,NO,NO2,O3,SO2,PM2.5,PM10,NH3";

    public static HashSet<string> Resolve(string? requested, HashSet<string> defaults, string supported)
    {
        if (requested == null) return defaults;
        var fields = requested.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = supported.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fields.Count == 0 || !fields.IsSubsetOf(allowed))
            throw new ModuleToolInputException("details must be a comma-separated selection of: " + supported);
        return fields;
    }
}
