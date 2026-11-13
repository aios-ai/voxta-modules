using System.Collections.Concurrent;
using BergamotTranslatorSharp;
using Microsoft.Extensions.Logging;

namespace Voxta.Modules.Translator.Translation;

public sealed class BergamotTranslationEngine(ModelStore modelStore, ILogger<BergamotTranslationEngine> logger)
    : ITranslationEngine, IDisposable
{
    public const string EngineId = "bergamot";

    private readonly ConcurrentDictionary<string, Lazy<Task<LoadedTranslationRoute>>> _routes =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public string Id => EngineId;
    public string Label => "Bergamot (local)";

    public Task<IReadOnlyList<TranslationLanguage>> GetSupportedLanguagesAsync(
        CancellationToken cancellationToken) => modelStore.GetSupportedLanguagesAsync(cancellationToken);

    public async ValueTask<string> TranslateAsync(
        string text,
        string fromLanguage,
        string toLanguage,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var from = BergamotLanguageCode.Normalize(fromLanguage);
        var to = BergamotLanguageCode.Normalize(toLanguage);
        if (!BergamotLanguageCode.IsValid(from) || !BergamotLanguageCode.IsValid(to))
        {
            logger.LogWarning("Invalid translation language code for {FromLanguage}->{ToLanguage}; using the original text", from, to);
            return text;
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
            return text;

        var routeKey = from + "-" + to;
        var routeFactory = new Lazy<Task<LoadedTranslationRoute>>(
            () => LoadRouteAsync(from, to, cancellationToken),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var lazyRoute = _routes.GetOrAdd(routeKey, routeFactory);

        try
        {
            var route = await lazyRoute.Value.WaitAsync(cancellationToken);
            return await route.TranslateAsync(text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (lazyRoute.IsValueCreated && lazyRoute.Value.IsCanceled)
                _routes.TryRemove(routeKey, out _);
            throw;
        }
        catch (Exception exception)
        {
            _routes.TryRemove(routeKey, out _);
            logger.LogError(
                exception,
                "Translation failed for {FromLanguage}->{ToLanguage}; using the original text",
                from,
                to);
            return text;
        }
    }

    private async Task<LoadedTranslationRoute> LoadRouteAsync(
        string fromLanguage,
        string toLanguage,
        CancellationToken cancellationToken)
    {
        var configPaths = await modelStore.EnsureRouteAsync(fromLanguage, toLanguage, cancellationToken);
        logger.LogInformation(
            "Loading Bergamot route {FromLanguage}->{ToLanguage} with {ModelCount} model(s)",
            fromLanguage,
            toLanguage,
            configPaths.Count);

        var service = configPaths.Count switch
        {
            1 => new BlockingService(configPaths[0]),
            2 => new BlockingService(configPaths[0], configPaths[1]),
            _ => throw new InvalidOperationException("A Bergamot route must contain one or two models."),
        };
        return new LoadedTranslationRoute(service);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (var lazyRoute in _routes.Values)
        {
            if (lazyRoute.IsValueCreated && lazyRoute.Value.IsCompletedSuccessfully)
                lazyRoute.Value.Result.Dispose();
        }
        _routes.Clear();
    }

    private sealed class LoadedTranslationRoute(BlockingService service) : IDisposable
    {
        private readonly SemaphoreSlim _translationLock = new(1, 1);

        public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            await _translationLock.WaitAsync(cancellationToken);
            try
            {
                return await Task.Run(() => service.Translate(text), CancellationToken.None);
            }
            finally
            {
                _translationLock.Release();
            }
        }

        public void Dispose()
        {
            service.Dispose();
            _translationLock.Dispose();
        }
    }
}
