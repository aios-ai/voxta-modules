using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Voxta.Modules.Translator.Translation;

public sealed class ModelStore(string dataFolder, HttpClient http, ILogger<ModelStore> logger)
{
    private const string RegistryUrl =
        "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json";
    private static readonly TimeSpan RegistryCacheLifetime = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _registryLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private RegistryDocument? _registry;

    private string ModelsRoot => Path.Combine(dataFolder, "models");
    private string RegistryCachePath => Path.Combine(dataFolder, "model-registry.json");

    public async Task<IReadOnlyList<TranslationLanguage>> GetSupportedLanguagesAsync(
        CancellationToken cancellationToken)
    {
        var registry = await GetRegistryAsync(cancellationToken);
        var releaseDirections = registry.Models
            .Where(entry => SelectRelease(entry.Value) is not null)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var languages = releaseDirections
            .SelectMany(ParseDirection)
            .Append("en")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(language =>
                string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ||
                releaseDirections.Contains(language + "-en") && releaseDirections.Contains("en-" + language))
            .Select(language => new TranslationLanguage(language, GetLanguageLabel(language)))
            .OrderBy(language => language.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return languages;
    }

    public async Task<IReadOnlyList<string>> EnsureRouteAsync(
        string fromLanguage,
        string toLanguage,
        CancellationToken cancellationToken)
    {
        var registry = await GetRegistryAsync(cancellationToken);
        var directDirection = fromLanguage + "-" + toLanguage;
        if (TryGetRelease(registry, directDirection, out var directModel))
            return [await EnsureModelAsync(registry, directDirection, directModel, cancellationToken)];

        var firstDirection = fromLanguage + "-en";
        var secondDirection = "en-" + toLanguage;
        if (!string.Equals(fromLanguage, "en", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toLanguage, "en", StringComparison.OrdinalIgnoreCase) &&
            TryGetRelease(registry, firstDirection, out var firstModel) &&
            TryGetRelease(registry, secondDirection, out var secondModel))
        {
            return
            [
                await EnsureModelAsync(registry, firstDirection, firstModel, cancellationToken),
                await EnsureModelAsync(registry, secondDirection, secondModel, cancellationToken),
            ];
        }

        throw new InvalidOperationException(
            $"No released translation route is available for '{fromLanguage}' to '{toLanguage}'.");
    }

    private async Task<string> EnsureModelAsync(
        RegistryDocument registry,
        string direction,
        RegistryModel model,
        CancellationToken cancellationToken)
    {
        var modelLock = _modelLocks.GetOrAdd(direction, _ => new SemaphoreSlim(1, 1));
        await modelLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.Combine(ModelsRoot, direction);
            Directory.CreateDirectory(directory);

            var modelFile = await DownloadFileAsync(
                registry,
                model.Files.Model,
                directory,
                ModelFileKind.Model,
                cancellationToken);
            var sharedVocab = model.Files.Vocab;
            var sourceVocab = model.Files.SourceVocab ?? sharedVocab ??
                throw new InvalidOperationException($"Model '{direction}' has no source vocabulary.");
            var targetVocab = model.Files.TargetVocab ?? sharedVocab ??
                throw new InvalidOperationException($"Model '{direction}' has no target vocabulary.");
            var sourceVocabFile = await DownloadFileAsync(
                registry,
                sourceVocab,
                directory,
                ModelFileKind.Vocabulary,
                cancellationToken);
            var targetVocabFile = ReferenceEquals(sourceVocab, targetVocab)
                ? sourceVocabFile
                : await DownloadFileAsync(
                    registry,
                    targetVocab,
                    directory,
                    ModelFileKind.Vocabulary,
                    cancellationToken);
            var shortlistFile = model.Files.LexicalShortlist is null
                ? null
                : await DownloadFileAsync(
                    registry,
                    model.Files.LexicalShortlist,
                    directory,
                    ModelFileKind.Shortlist,
                    cancellationToken);

            var configPath = Path.Combine(directory, "config.yml");
            var config = BuildConfig(modelFile, sourceVocabFile, targetVocabFile, shortlistFile);
            await WriteAtomicAsync(configPath, config, cancellationToken);
            return configPath;
        }
        finally
        {
            modelLock.Release();
        }
    }

    private async Task<string> DownloadFileAsync(
        RegistryDocument registry,
        RegistryFile file,
        string directory,
        ModelFileKind kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file.Path))
            throw new InvalidOperationException("The model registry contains an empty file path.");

        var fileName = Path.GetFileName(file.Path);
        if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            fileName = fileName[..^3];
        var targetPath = Path.Combine(directory, fileName);

        if (await IsValidCachedFileAsync(targetPath, file, kind, cancellationToken))
            return targetPath;

        var downloadUri = CreateDownloadUri(registry.BaseUrl, file.Path);
        var temporaryPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        logger.LogInformation("Downloading translation model asset {FileName}", fileName);

        try
        {
            using var response = await http.GetAsync(
                downloadUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            {
                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (file.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                {
                    await using var gzip = new GZipStream(source, CompressionMode.Decompress, true);
                    await gzip.CopyToAsync(destination, cancellationToken);
                }
                else
                {
                    await source.CopyToAsync(destination, cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
            }
            ValidateFile(temporaryPath, file, kind);
            if (!string.IsNullOrWhiteSpace(file.UncompressedHash) &&
                !await FileMatchesHashAsync(temporaryPath, file.UncompressedHash, cancellationToken))
            {
                throw new InvalidDataException($"Checksum validation failed for '{fileName}'.");
            }

            File.Move(temporaryPath, targetPath, true);
            return targetPath;
        }
        catch (HttpRequestException exception) when (exception.StatusCode is not null)
        {
            throw new InvalidOperationException(
                $"Downloading model asset '{fileName}' failed with HTTP {(int)exception.StatusCode.Value}.",
                exception);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task<bool> IsValidCachedFileAsync(
        string path,
        RegistryFile file,
        ModelFileKind kind,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            ValidateFile(path, file, kind);
            return string.IsNullOrWhiteSpace(file.UncompressedHash) ||
                await FileMatchesHashAsync(path, file.UncompressedHash, cancellationToken);
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void ValidateFile(string path, RegistryFile file, ModelFileKind kind)
    {
        var info = new FileInfo(path);
        if (info.Length < 4)
            throw new InvalidDataException($"Model asset '{info.Name}' is empty or truncated.");
        if (file.UncompressedSize is > 0 && info.Length != file.UncompressedSize.Value)
        {
            throw new InvalidDataException(
                $"Model asset '{info.Name}' has {info.Length} bytes; expected {file.UncompressedSize.Value}.");
        }

        Span<byte> magic = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        if (stream.Read(magic) != magic.Length)
            throw new InvalidDataException($"Model asset '{info.Name}' is truncated.");

        switch (kind)
        {
            case ModelFileKind.Model when info.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase):
                if (magic[0] != 0x00 || magic[1] != 0xFD)
                    throw new InvalidDataException($"Model asset '{info.Name}' is not an intgemm model.");
                break;
            case ModelFileKind.Model when info.Name.EndsWith(".npz", StringComparison.OrdinalIgnoreCase):
                if (magic[0] != 0x50 || magic[1] != 0x4B)
                    throw new InvalidDataException($"Model asset '{info.Name}' is not an NPZ archive.");
                break;
            case ModelFileKind.Vocabulary:
                if (magic[0] != 0x0A)
                    throw new InvalidDataException($"Model asset '{info.Name}' is not a SentencePiece vocabulary.");
                break;
            case ModelFileKind.Shortlist:
                if (magic[0] == 0 && magic[1] == 0 && magic[2] == 0 && magic[3] == 0)
                    throw new InvalidDataException($"Model asset '{info.Name}' contains an invalid shortlist.");
                break;
        }
    }

    private async Task<RegistryDocument> GetRegistryAsync(CancellationToken cancellationToken)
    {
        if (_registry is not null)
            return _registry;

        await _registryLock.WaitAsync(cancellationToken);
        try
        {
            if (_registry is not null)
                return _registry;

            Directory.CreateDirectory(dataFolder);
            var cached = await ReadCachedRegistryAsync(cancellationToken);
            if (cached is not null &&
                DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(RegistryCachePath) < RegistryCacheLifetime)
            {
                return _registry = cached;
            }

            try
            {
                using var response = await http.GetAsync(
                    RegistryUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var registry = DeserializeRegistry(json);
                await WriteAtomicAsync(RegistryCachePath, json, cancellationToken);
                return _registry = registry;
            }
            catch (Exception exception) when (
                cached is not null &&
                exception is HttpRequestException or IOException or JsonException)
            {
                logger.LogWarning(exception, "Could not refresh the model registry; using the cached copy");
                return _registry = cached;
            }
        }
        finally
        {
            _registryLock.Release();
        }
    }

    private async Task<RegistryDocument?> ReadCachedRegistryAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RegistryCachePath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(RegistryCachePath, cancellationToken);
            return DeserializeRegistry(json);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
        {
            logger.LogWarning(exception, "Ignoring an invalid cached translation model registry");
            return null;
        }
    }

    private static RegistryDocument DeserializeRegistry(string json)
    {
        var registry = JsonSerializer.Deserialize<RegistryDocument>(json, JsonOptions) ??
            throw new InvalidDataException("The translation model registry was empty.");
        if (!Uri.TryCreate(registry.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            registry.Models.Count == 0)
        {
            throw new InvalidDataException("The translation model registry is invalid.");
        }
        return registry;
    }

    private static Uri CreateDownloadUri(string baseUrl, string relativePath)
    {
        var baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var uri = new Uri(baseUri, relativePath.TrimStart('/'));
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != baseUri.Host)
            throw new InvalidDataException("The model registry contains an unsafe download URL.");
        return uri;
    }

    private static string BuildConfig(
        string modelPath,
        string sourceVocabPath,
        string targetVocabPath,
        string? shortlistPath)
    {
        var shortlist = shortlistPath is null
            ? string.Empty
            : $"shortlist:\n- {Path.GetFileName(shortlistPath)}\n- false\n";
        var precision = Path.GetFileName(modelPath).Contains("alphas", StringComparison.OrdinalIgnoreCase)
            ? "int8shiftAlphaAll"
            : "int8shiftAll";

        return $"""
            relative-paths: true
            models:
            - {Path.GetFileName(modelPath)}
            vocabs:
            - {Path.GetFileName(sourceVocabPath)}
            - {Path.GetFileName(targetVocabPath)}
            {shortlist}beam-size: 1
            normalize: 1.0
            word-penalty: 0
            max-length-break: 128
            mini-batch-words: 1024
            workspace: 128
            max-length-factor: 2.0
            skip-cost: true
            cpu-threads: 0
            quiet: true
            quiet-translation: true
            gemm-precision: {precision}
            """;
    }

    private static async Task WriteAtomicAsync(
        string targetPath,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = targetPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, targetPath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static async Task<bool> FileMatchesHashAsync(
        string path,
        string expectedHex,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetRelease(
        RegistryDocument registry,
        string direction,
        out RegistryModel model)
    {
        if (registry.Models.TryGetValue(direction, out var candidates) && SelectRelease(candidates) is { } release)
        {
            model = release;
            return true;
        }
        model = null!;
        return false;
    }

    private static RegistryModel? SelectRelease(IEnumerable<RegistryModel> candidates) =>
        candidates.LastOrDefault(candidate =>
            string.Equals(candidate.ReleaseStatus, "Release", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ParseDirection(string direction)
    {
        var separator = direction.IndexOf('-');
        if (separator <= 0 || separator >= direction.Length - 1)
            return [];
        return [direction[..separator], direction[(separator + 1)..]];
    }

    private static string GetLanguageLabel(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).EnglishName + " (" + code + ")";
        }
        catch (CultureNotFoundException)
        {
            return code.ToUpperInvariant();
        }
    }

    private enum ModelFileKind
    {
        Model,
        Vocabulary,
        Shortlist,
    }

    private sealed class RegistryDocument
    {
        public string BaseUrl { get; init; } = string.Empty;
        public Dictionary<string, List<RegistryModel>> Models { get; init; } = [];
    }

    private sealed class RegistryModel
    {
        [JsonPropertyName("releaseStatus")]
        public string? ReleaseStatus { get; init; }
        public RegistryFiles Files { get; init; } = new();
    }

    private sealed class RegistryFiles
    {
        public RegistryFile Model { get; init; } = new();
        public RegistryFile? Vocab { get; init; }
        [JsonPropertyName("srcVocab")]
        public RegistryFile? SourceVocab { get; init; }
        [JsonPropertyName("trgVocab")]
        public RegistryFile? TargetVocab { get; init; }
        [JsonPropertyName("lexicalShortlist")]
        public RegistryFile? LexicalShortlist { get; init; }
    }

    private sealed class RegistryFile
    {
        public string Path { get; init; } = string.Empty;
        [JsonPropertyName("uncompressedHash")]
        public string? UncompressedHash { get; init; }
        [JsonPropertyName("uncompressedSize")]
        public long? UncompressedSize { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
