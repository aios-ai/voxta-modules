namespace Voxta.Modules.Translator.Translation;

public sealed class TranslatorEngineRegistry(IEnumerable<ITranslationEngine> engines)
{
    private readonly IReadOnlyDictionary<string, ITranslationEngine> _engines = engines
        .ToDictionary(engine => engine.Id, StringComparer.OrdinalIgnoreCase);

    public ITranslationEngine GetRequired(string id) =>
        _engines.TryGetValue(id, out var engine)
            ? engine
            : throw new InvalidOperationException($"Translation engine '{id}' is not available.");

    public IReadOnlyCollection<ITranslationEngine> All => _engines.Values.ToArray();
}
