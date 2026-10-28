using System.Globalization;
using Voxta.Modules.Aios.OpenWeather.Clients;

namespace Voxta.Modules.Aios.OpenWeather.Tools;

internal static class WeatherMapPresentation
{
    // Local reference from the default legend linked by the Weather Maps 1.0 documentation.
    // https://openweathermap.org/api/map_legend — no runtime legend or weather-data request.
    public static string Legend(string layer) => layer switch
    {
        "precipitation_new" => "Precipitation, mm (interval unspecified): 0–0.5 transparent; " +
            "1 muted blue #6E6ECD; 10 blue #5050E1; 140 deep blue #1414FF. " +
            "Transparent areas do not establish zero precipitation or clear skies; rain/snow type is not identified.",
        "clouds_new" => "Cloud cover, %: 0 transparent; 50 nearly white #F7F7FF; " +
            "70 gray-white #F4F4FF; 100 gray-white #F0F0FF. Cloud cover does not establish precipitation.",
        "temp_new" => "Temperature, °C: -40 purple #821692; -20 blue #208CEC; 0 cyan #23DDDD; " +
            "10 yellow-green #C2FF28; 20 yellow #FFF028; 30 orange #FC8014.",
        "pressure_new" => "Sea-level pressure, hPa: 940 blue #0073FF; 1000 mint #8DE7C7; " +
            "1020 amber #F0B800; 1080 red #C60000.",
        "wind_new" => "Wind speed, m/s: 1 transparent; 5 pale pink #EECECE; 15 purple #B364BC; " +
            "25 dark purple #3F213B; 50 violet #744CAC; 100 deep purple #4600AF; " +
            "200 near-black blue #0D1126. Wind direction is not shown.",
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    public static string Describe(WeatherMapImage map, (MapTargetType Type, string Identifier) target, string layer)
    {
        var v = map.Viewport;
        static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        return $"""
            Weather map: {target.Type} {target.Identifier}, {WeatherMapHelper.ToDisplayName(layer)} ({layer}). Image returned for vision; chat attachment queued.
            Image: {v.Width} × {v.Height}px; zoom {v.Zoom}; north up, east right.
            Bounds (degrees): west {Number(v.West)}, south {Number(v.South)}, east {Number(v.East)}, north {Number(v.North)}.{(v.CrossesAntimeridian ? " Crosses the antimeridian." : "")} Neighboring territory may be visible; focus on {target.Identifier}.
            Time: current layer; observation time unknown. Composite cache reused: {map.CompositeFromCache.ToString().ToLowerInvariant()}; tiles may also be cached.
            Color legend (fixed map units): {Legend(layer)}
            Briefly describe visible patterns in qualitative terms. Colors blend with the base map; use the legend only if it matches. Avoid exact readings, regional averages or inferred conditions from other layers. If you cannot view or interpret the image reliably, say so.
            """;
    }
}
