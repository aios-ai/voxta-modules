using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Translator.ChatAugmentations;
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
    Check(languages.Select(language => language.Code).SequenceEqual(["zh_hant", "en", "fr", "de"]),
        "Only bidirectional released languages are listed with readable sorting");
    Check(languages.Single(language => language.Code == "de").Label == "German (de)",
        "Language labels are readable");
    Check(languages[0].Label.Contains("Traditional", StringComparison.Ordinal),
        "Traditional Chinese has a readable script label");

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

    var restartedStore = new ModelStore(tempRoot, new HttpClient(handler), NullLogger<ModelStore>.Instance);
    var restartedRoute = await restartedStore.EnsureRouteAsync("de", "fr", default);
    Check(restartedRoute.SequenceEqual(route) && handler.AssetRequests == assetRequests,
        "A fresh store reuses persisted models after restart without downloading assets");

    var cachedModel = Directory.GetFiles(Path.GetDirectoryName(route[0])!, "model.*.bin").Single();
    var damagedModel = await File.ReadAllBytesAsync(cachedModel);
    damagedModel[^1] ^= 0xFF;
    await File.WriteAllBytesAsync(cachedModel, damagedModel);
    var repairStore = new ModelStore(tempRoot, new HttpClient(handler), NullLogger<ModelStore>.Instance);
    await repairStore.EnsureRouteAsync("de", "fr", default);
    Check(handler.AssetRequests == assetRequests + 1,
        "Checksum mismatch repairs only the damaged asset");

    var invalidHandler = new RegistryHandler(invalidBinary: true);
    var invalidStore = new ModelStore(Path.Combine(tempRoot, "invalid"), new HttpClient(invalidHandler), NullLogger<ModelStore>.Instance);
    var invalidRejected = false;
    try
    {
        await invalidStore.EnsureRouteAsync("de", "en", default);
    }
    catch (InvalidDataException)
    {
        invalidRejected = true;
    }
    Check(invalidRejected, "Downloaded temporary files are format-validated even when their checksum matches");
    Check(!Directory.GetFiles(Path.Combine(tempRoot, "invalid", "models"), "*.bin", SearchOption.AllDirectories).Any(),
        "Invalid downloads are never promoted into the cache");

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
        Check(languageField.Choices!.Select(choice => choice.Value).SequenceEqual(languages.Select(language => language.Code)),
            fieldName + " only offers supported languages");
    }
    var beforeInvalidCode = handler.AssetRequests;
    Check(await engine.TranslateAsync("Original", "../en", "fr", default) == "Original" &&
        handler.AssetRequests == beforeInvalidCode, "Malformed language codes leave the message intact");
    Check(await engine.TranslateAsync("原文", "zh-Hant", "zh_hant", default) == "原文" &&
        handler.AssetRequests == beforeInvalidCode, "Traditional Chinese aliases resolve to the same language");
    engine.Dispose();

    var traditionalRoute = await store.EnsureRouteAsync("zh_hant", "fr", default);
    Check(traditionalRoute.Count == 2 && traditionalRoute[0].Contains("zh_hant-en"),
        "Traditional Chinese can pivot through English");
    var traditionalReply = await store.EnsureRouteAsync("en", "zh_hant", default);
    Check(traditionalReply.Count == 1 && traditionalReply[0].Contains("en-zh_hant"),
        "Traditional Chinese is available as a reply language");

    var speechOnlyField = fields.OfType<FormBooleanField>()
        .Single(field => field.Name == ModuleConfigurationProvider.TranslateSpeechOnlyName);
    var oldSettings = new StaticSettingsSource(new());
    Check(!oldSettings.GetRequired(speechOnlyField), "Existing configurations default to normal reply translation");
    var enabledSettings = new StaticSettingsSource(new() { [speechOnlyField.Name] = "true" });
    Check(enabledSettings.GetRequired(speechOnlyField) && speechOnlyField.Label == "Translate speech only",
        "The speech-only option can be enabled and has a stable label");

    var session = DispatchProxy.Create<IChatSessionChatAugmentationApi, TranslatorSessionProxy>();
    var fakeEngine = new RecordingTranslationEngine();
    var normal = new TranslatorAugmentation(session, fakeEngine, "de", "fr", NullLogger.Instance);
    Check(await normal.PostProcessTextAsync(ChatMessageRole.Assistant, "Hello", default) == "fr:Hello",
        "Default mode still translates the assistant response");
    var beforeSpeech = fakeEngine.Calls;
    Check(normal.SpeechProcessText("fr:Hello") == "fr:Hello" && fakeEngine.Calls == beforeSpeech,
        "Default mode does not translate speech a second time");

    var experimental = new TranslatorAugmentation(session, fakeEngine, "de", "fr", NullLogger.Instance, true);
    Check(await experimental.PostProcessTextAsync(ChatMessageRole.Assistant, "Hello", default) == "Hello",
        "Experimental mode preserves the original response for chat/history");
    Check(fakeEngine.Calls == beforeSpeech, "Preserving chat text does not invoke translation");
    Check(experimental.SpeechProcessText("Hello") == "fr:Hello", "Speech is translated into the existing reply language");
    Check(fakeEngine.LastRoute == "en-fr", "Speech translation uses the character language");
    Check(await experimental.ProcessInboundTextAsync(new(InboundTextKind.Message, "User"), "Hallo", default) == "en:Hallo",
        "Experimental mode retains the existing inbound translation");
    Check(fakeEngine.LastRoute == "de-en", "Inbound translation uses the existing user language");
    var sameLanguage = new TranslatorAugmentation(session, fakeEngine, "de", "en", NullLogger.Instance, true);
    var beforeNoOp = fakeEngine.Calls;
    Check(sameLanguage.SpeechProcessText("Hello") == "Hello" && experimental.SpeechProcessText(" ") == " ",
        "Matching languages and empty speech are unchanged");
    Check(await normal.PostProcessTextAsync(ChatMessageRole.User, "Hallo", default) == "Hallo" && fakeEngine.Calls == beforeNoOp,
        "No-op paths do not translate");
    fakeEngine.EmptyResult = true;
    Check(experimental.SpeechProcessText("Hello") == "Hello", "Empty translation falls back to original speech");

    Console.WriteLine("PASS Translator language catalog, pivot models, cache reuse, configuration, and speech-only routing");
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

public class TranslatorSessionProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_MainCharacter" ? null : throw new NotSupportedException(targetMethod?.Name);
}

sealed class RecordingTranslationEngine : ITranslationEngine
{
    public string Id => "test";
    public string Label => "Test";
    public int Calls { get; private set; }
    public string? LastRoute { get; private set; }
    public bool EmptyResult { get; set; }
    public Task<IReadOnlyList<TranslationLanguage>> GetSupportedLanguagesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranslationLanguage>>([]);

    public async ValueTask<string> TranslateAsync(string text, string fromLanguage, string toLanguage, CancellationToken cancellationToken)
    {
        await Task.Yield();
        Calls++;
        LastRoute = fromLanguage + "-" + toLanguage;
        return EmptyResult ? "" : toLanguage + ":" + text;
    }
}

sealed class RegistryHandler : HttpMessageHandler
{
    private readonly Dictionary<string, byte[]> _assets = new(StringComparer.Ordinal);
    private readonly string _registry;
    private readonly bool _invalidBinary;

    public RegistryHandler(bool invalidBinary = false)
    {
        _invalidBinary = invalidBinary;
        var models = new Dictionary<string, object>();
        AddRelease(models, "de-en");
        AddRelease(models, "en-de");
        AddRelease(models, "fr-en");
        AddRelease(models, "en-fr");
        AddRelease(models, "zh_hant-en");
        AddRelease(models, "en-zh_hant");
        AddRelease(models, "en-zh");
        AddRelease(models, "xx_!-en");
        AddRelease(models, "en-xx_!");
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
        var model = new byte[49];
        BinaryPrimitives.WriteUInt64LittleEndian(model, _invalidBinary ? 2UL : 1UL);
        BinaryPrimitives.WriteUInt64LittleEndian(model.AsSpan(8), 1);
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
