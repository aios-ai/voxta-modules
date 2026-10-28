using System.Net;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Voxta.Modules.Aios.OpenWeather.Clients;

internal static class WeatherMapChecks
{
    public static async Task RunAsync()
    {
        var cache = Path.Combine(Path.GetTempPath(), "Voxta.OpenWeather.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var transport = new TileTransport();
            using var http = new HttpClient(transport);
            var logger = new MapLogger();
            var client = new OpenWeatherClient(http, "test-key", logger);
            var result = await client.FetchWeatherMapAsync((MapTargetType.Global, "Global"), "temp_new", cache, default);
            Check(result.Success && result.Value is { Bytes.Length: > 0 }, "Real compositor returns PNG data: " + logger.Error);
            using var map = Image.Load<Rgba32>(result.Value!.Bytes);
            Check(map.Width == 1024 && map.Height == 1024, "Global tile grid is stitched correctly");
            Check(map[20, 20].B > map[20, 20].R, "Weather overlay is drawn on the base map");
            Check(transport.Requests == 32, "Sixteen base tiles and sixteen overlays fetched once");
            var count = transport.Requests;
            var cached = await client.FetchWeatherMapAsync((MapTargetType.Global, "Global"), "temp_new", cache, default);
            Check(cached.Success && cached.Value!.Bytes.SequenceEqual(result.Value!.Bytes) && transport.Requests == count, "Composite cache preserves PNG without more requests");

            Check(result.Value!.Viewport == cached.Value!.Viewport && !result.Value.CompositeFromCache && cached.Value.CompositeFromCache, "Cache retains identical viewport and reports image reuse");
            var viewport = result.Value.Viewport;
            Check(viewport.West == -180 && viewport.East == 180 && Math.Abs(viewport.North - 85.05112878) < 0.0001
                && Math.Abs(viewport.South + 85.05112878) < 0.0001 && !viewport.CrossesAntimeridian, "Actual global Mercator viewport");
            Check(viewport.Width == map.Width && viewport.Height == map.Height && viewport.Zoom == 2, "Viewport dimensions match the generated image");
            var country = await client.FetchWeatherMapAsync((MapTargetType.Country, "DE"), "temp_new", cache, default);
            Check(country.Success, "Country composition succeeds");
            var cv = country.Value!.Viewport;
            CountryCentroids.TryGetBounds("DE", out var bounds);
            Check(cv.West <= bounds.MinLon && cv.East >= bounds.MaxLon && cv.South <= bounds.MinLat && cv.North >= bounds.MaxLat, "Rendered country viewport covers requested bounds");
            using var countryImage = Image.Load<Rgba32>(country.Value.Bytes);
            Check(countryImage.Width == cv.Width && countryImage.Height == cv.Height, "Country viewport matches actual tile rectangle");
            var crossing = await client.FetchWeatherMapAsync((MapTargetType.Country, "FJ"), "temp_new", cache, default);
            Check(crossing.Success && crossing.Value!.Viewport.CrossesAntimeridian && crossing.Value.Viewport.West > crossing.Value.Viewport.East, "Actual date-line country viewport is represented correctly");

            transport.Fail = true;
            var failed = await client.FetchWeatherMapAsync((MapTargetType.Country, "DE"), "wind_new", cache, default);
            Check(!failed.Success && failed.Value == null && !string.IsNullOrWhiteSpace(failed.UserVisibleError), "Tile failure cannot become successful map output");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await client.FetchWeatherMapAsync((MapTargetType.Global, "Global"), "temp_new", cache, cancelled.Token);
                throw new Exception("Map compositor swallowed cancellation");
            }
            catch (OperationCanceledException) { }
            Console.WriteLine("PASS real map PNG composition, overlay drawing, dimensions, cache, provider failure and cancellation using fake HTTP tiles");
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class MapLogger : ILogger<OpenWeatherClient>
    {
        public string? Error;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Error = formatter(state, exception) + " " + exception;
        }
    }

    private sealed class TileTransport : HttpMessageHandler
    {
        public int Requests;
        public bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            if (Fail) return new(HttpStatusCode.Forbidden) { Content = new StringContent("Test tile failure") };
            var isBase = request.RequestUri!.Host == "tile.openstreetmap.org";
            using var tile = new Image<Rgba32>(256, 256, isBase ? new Rgba32(220, 220, 220) : new Rgba32(0, 0, 255, 128));
            using var output = new MemoryStream();
            await tile.SaveAsPngAsync(output, cancellationToken);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(output.ToArray()) };
        }
    }
}
