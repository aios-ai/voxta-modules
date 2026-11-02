namespace Voxta.Modules.Translator.Translation;

public readonly record struct TranslationLanguage(string Code, string Label);

public interface ITranslationEngine
{
    string Id { get; }
    string Label { get; }
    Task<IReadOnlyList<TranslationLanguage>> GetSupportedLanguagesAsync(CancellationToken cancellationToken);
    ValueTask<string> TranslateAsync(string text, string fromLanguage, string toLanguage, CancellationToken cancellationToken);
}
