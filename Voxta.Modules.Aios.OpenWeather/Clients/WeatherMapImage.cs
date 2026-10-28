namespace Voxta.Modules.Aios.OpenWeather.Clients;

public sealed record WeatherMapImage(byte[] Bytes, WeatherMapViewport Viewport, bool CompositeFromCache);

/// <summary>The full stitched tile rectangle, including any area outside the requested country.</summary>
public sealed record WeatherMapViewport(
    double West, double South, double East, double North,
    bool CrossesAntimeridian, int Zoom, int Width, int Height);
