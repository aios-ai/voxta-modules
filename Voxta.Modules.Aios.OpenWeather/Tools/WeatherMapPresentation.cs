using System.Globalization;
using Voxta.Modules.Aios.OpenWeather.Clients;

namespace Voxta.Modules.Aios.OpenWeather.Tools;

internal static class WeatherMapPresentation
{
    // Local reference from the default legend linked by the Weather Maps 1.0 documentation.
    // https://openweathermap.org/api/map_legend — no runtime legend or weather-data request.
    public static string Legend(string layer) => layer switch
    {
        "precipitation_new" => "Precipitation (mm, as labeled by the provider; no accumulation interval is supplied). " +
            "Default rain reference: 0–0.5 mm transparent; 1 mm muted blue (#6E6ECD, alpha 0.3); " +
            "10 mm blue (#5050E1, alpha 0.7); 140 mm deep blue (#1414FF, alpha 0.9). " +
            "Fainter/transparent areas can hide small amounts; they do not establish zero precipitation. " +
            "This layer alone does not identify rain versus snow or establish clear skies.",
        "clouds_new" => "Cloud cover (%). Default reference: 0% transparent; " +
            "50% nearly white (#F7F7FF, alpha 0.5); 70% pale gray-white (#F4F4FF, alpha 1); " +
            "100% pale gray-white (#F0F0FF, alpha 1). Distinguish the overlay from pale base-map features; " +
            "cloud cover alone does not establish precipitation.",
        "temp_new" => "Temperature (°C; map colors do not change with configured metric/imperial report units). " +
            "Default reference: -40°C purple (#821692); -20°C blue (#208CEC); 0°C cyan (#23DDDD); " +
            "10°C yellow-green (#C2FF28); 20°C yellow (#FFF028); 30°C orange (#FC8014).",
        "pressure_new" => "Sea-level pressure (hPa; reference values converted from the provider's Pa legend). " +
            "Default reference: 940 hPa blue (#0073FF); 1000 hPa mint (#8DE7C7); " +
            "1020 hPa amber (#F0B800); 1080 hPa red (#C60000).",
        "wind_new" => "Wind speed (m/s; map colors do not change with configured metric/imperial report units). " +
            "Default reference: 1 m/s transparent; 5 m/s pale pink (#EECECE, alpha 0.4); " +
            "15 m/s purple (#B364BC, alpha 0.7); 25 m/s dark purple (#3F213B, alpha 0.8); " +
            "50 m/s violet (#744CAC, alpha 0.9); 100 m/s deep purple (#4600AF); " +
            "200 m/s near-black blue (#0D1126). This speed layer supplies no wind direction.",
        _ => throw new ArgumentOutOfRangeException(nameof(layer)),
    };

    public static string Describe(WeatherMapImage map, (MapTargetType Type, string Identifier) target, string layer)
    {
        var v = map.Viewport;
        static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        return $"""
            Weather map for {target.Identifier}: {WeatherMapHelper.ToDisplayName(layer)}. The map image was attached to the conversation and is included in this tool result for vision.
            Requested target: {target.Type}, {target.Identifier}. Provider: OpenWeather Weather Maps 1.0; layer: {layer}. Base map: OpenStreetMap.
            Image: {v.Width} × {v.Height} pixels; Web Mercator zoom {v.Zoom}; north up, east to the right.
            Rendered viewport (degrees): west {Number(v.West)}, south {Number(v.South)}, east {Number(v.East)}, north {Number(v.North)}. Crosses the antimeridian: {v.CrossesAntimeridian.ToString().ToLowerInvariant()}.
            The viewport is the entire tile rectangle, not a country boundary mask; neighboring countries or seas can be visible. Restrict regional claims to identifiable parts of the requested target.
            Time: current-layer request, not a dated forecast. The provider observation time is not supplied. Composite reused from cache: {map.CompositeFromCache.ToString().ToLowerInvariant()}; underlying weather tiles may also be cached. This is not a claim of live radar or a fresh observation.
            Color legend: {Legend(layer)}
            Legend source: https://openweathermap.org/api/map_legend (published default-style reference). Colors interpolate between reference stops and blend over the base map; these anchors are not measurements extracted from this image. If the visible palette does not match the reference, do not use its numeric anchors to infer intensity.
            Visual interpretation: if you can inspect the returned image, briefly describe the visible spatial pattern using this layer and legend. Use cautious qualitative terms and directions only where the geography is identifiable. Do not invent exact values, a region-wide average, an observation timestamp, or conditions not represented by this layer. If you cannot view the image or cannot distinguish a pattern reliably, say so instead of inventing a summary.
            """;
    }
}
