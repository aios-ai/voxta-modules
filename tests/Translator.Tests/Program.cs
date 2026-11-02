using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Translator.Configuration;
using Voxta.Modules.Translator.Translation;

var tempRoot = Path.Combine(Path.GetTempPath(), "voxta-translator-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);

try
{
    var handler = new RegistryHandler();
    var store = new ModelStore(tempRoot, new HttpClient(handler), NullLogger<ModelStore>.Instance);
    var languages = await store.GetSupportedLanguagesAsync(default);
    Check(languages.Select(language => language.Code).SequenceEqual(["en", "fr", "de"]),
        "Only bidirectional released languages are listed with readable sorting");
    Check(languages.Single(language => language.Code == "de").Label == "German (de)",
        "Language labels are readable");

    var route = await store.EnsureRouteAsync("de", "fr", default);
    Check(route.Count == 2, "A missing direct route pivots through English");
    Check(route[0].Contains("de-en") && route[1].Contains("en-fr"), "Pivot model order is source then target");
    foreach (var configPath in route)
    {
        var config = await File.ReadAllTextAsync(configPath);
        Check(config.Contains("shortlist:") && config.Contains("- false"), "Bergamot shortlist configuration");
        Check(config.Contains("gemm-precision: int8shiftAlphaAll"), "Bergamot model precision configuration");
    }

    var assetRequests = handler.AssetRequests;
    var cachedRoute = await store.EnsureRouteAsync("de", "fr", default);
    Check(cachedRoute.SequenceEqual(route) && handler.AssetRequests == assetRequests,
        "Validated model assets are reused without another download");

    var engine = new BergamotTranslationEngine(store, NullLogger<BergamotTranslationEngine>.Instance);
    var registry = new TranslatorEngineRegistry([engine]);
    var provider = new ModuleConfigurationProvider(registry, NullLogger<ModuleConfigurationProvider>.Instance);
    var fields = await provider.GetModuleConfigurationFieldsAsync(null!, null!, default);
    var engineField = fields.OfType<FormChoicesField>().Single(field => field.Name == ModuleConfigurationProvider.EngineName);
    Check(engineField.Choices!.Select(choice => choice.Value).SequenceEqual([BergamotTranslationEngine.EngineId]),
        "Translation engine is a constrained choice");
    foreach (var fieldName in new[] { ModuleConfigurationProvider.UserLanguageName, ModuleConfigurationProvider.ChatLanguageName })
    {
        var languageField = fields.OfType<FormChoicesField>().Single(field => field.Name == fieldName);
        Check(languageField.Choices!.Select(choice => choice.Value).SequenceEqual(["en", "fr", "de"]),
            fieldName + " only offers supported languages");
    }
    engine.Dispose();

    Console.WriteLine("PASS Translator language catalog, pivot models, cache reuse, and configuration choices");
}
finally
{
    Directory.Delete(tempRoot, true);
}

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("FAILED: " + message);
}

sealed class RegistryHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
    private readonly string _registry;

    public RegistryHandler()
    {
        var models = new Dictionary<string, object>();
        AddRelease(models, "de-en");
        AddRelease(models, "en-de");
        AddRelease(models, "fr-en");
        AddRelease(models, "en-fr");
        models["es-en"] = new[] { Candidate("es-en", "Nightly") };
        _registry = JsonSerializer.Serialize(new
        {
            baseUrl = "https://models.example.test",
            models,
        });
    }

    public int AssetRequests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/db/models.json", StringComparison.Ordinal))
            return Task.FromResult(Response(Encoding.UTF8.GetBytes(_registry), "application/json"));

        AssetRequests++;
        var path = request.RequestUri.AbsolutePath.TrimStart('/');
        if (!_assets.TryGetValue(path, out var content))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(Response(Gzip(content), "application/gzip"));
    }

    private void AddRelease(Dictionary<string, object> models, string direction) =>
        models[direction] = new[] { Candidate(direction, "Release") };

    private object Candidate(string direction, string status)
    {
        var model = new byte[] { 0x00, 0xFD, 0x00, 0x00, 0x01 };
        var vocab = new byte[] { 0x0A, 0x01, 0x02, 0x03, 0x04 };
        var shortlist = new byte[] { 0x01, 0x00, 0x00, 0x00, 0x05 };
        var prefix = "models/" + direction + "/";
        AddAsset(prefix + "model." + direction + ".intgemm.alphas.bin.gz", model);
        AddAsset(prefix + "vocab." + direction + ".spm.gz", vocab);
        AddAsset(prefix + "lex.50.50." + direction + ".s2t.bin.gz", shortlist);
        return new
        {
            releaseStatus = status,
            files = new
            {
                model = File(prefix + "model." + direction + ".intgemm.alphas.bin.gz", model),
                vocab = File(prefix + "vocab." + direction + ".spm.gz", vocab),
                lexicalShortlist = File(prefix + "lex.50.50." + direction + ".s2t.bin.gz", shortlist),
            },
        };
    }

    private void AddAsset(string path, byte[] content) => _assets[path] = content;

    private static object File(string path, byte[] content) => new
    {
        path,
        uncompressedSize = content.Length,
        uncompressedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
    };

    private static HttpResponseMessage Response(byte[] content, string mediaType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(content)
        {
            Headers = { ContentType = new(mediaType) },
        },
    };

    private static byte[] Gzip(byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            gzip.Write(content);
        return output.ToArray();
    }
}
