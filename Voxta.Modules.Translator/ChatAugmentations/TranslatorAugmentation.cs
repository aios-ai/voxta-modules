using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Translator.Translation;

namespace Voxta.Modules.Translator.ChatAugmentations;

public sealed class TranslatorAugmentation(
    IChatSessionChatAugmentationApi session,
    ITranslationEngine engine,
    string userLanguage,
    string replyLanguage,
    ILogger logger,
    bool translateSpeechOnly = false)
    : IChatInboundTextAugmentation, IChatPostProcessAugmentation, IChatSpeechReplacementsAugmentation
{
    private readonly string _userLanguage = NormalizeLanguage(userLanguage);
    private readonly string _replyLanguage = NormalizeLanguage(replyLanguage);

    public ServiceTypes[] GetRequiredServiceTypes() => [];

    private string CharacterLanguage => NormalizeLanguage(
        BergamotLanguageCode.FromCulture(session.MainCharacter?.Culture));

    public async ValueTask<string> ProcessInboundTextAsync(
        InboundTextSource source,
        string text,
        CancellationToken cancellationToken)
    {
        if (source.Kind != InboundTextKind.Message ||
            !string.Equals(source.Name, "User", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(text) ||
            string.Equals(_userLanguage, CharacterLanguage, StringComparison.Ordinal))
        {
            return text;
        }

        return await TranslateAsync(text, _userLanguage, CharacterLanguage, "inbound", cancellationToken);
    }

    public async ValueTask<string> PostProcessTextAsync(
        ChatMessageRole role,
        string text,
        CancellationToken cancellationToken)
    {
        if (translateSpeechOnly || role != ChatMessageRole.Assistant ||
            string.IsNullOrWhiteSpace(text) ||
            string.Equals(CharacterLanguage, _replyLanguage, StringComparison.Ordinal))
        {
            return text;
        }

        return await TranslateAsync(text, CharacterLanguage, _replyLanguage, "reply", cancellationToken);
    }

    public string SpeechProcessText(string text)
    {
        var characterLanguage = CharacterLanguage;
        if (!translateSpeechOnly || string.IsNullOrWhiteSpace(text) ||
            string.Equals(characterLanguage, _replyLanguage, StringComparison.Ordinal))
        {
            return text;
        }

        // Voxta's speech hook is synchronous. Run the async engine off the caller's
        // synchronization context, then wait for the text required by speech synthesis.
        // The hook provides no cancellation token.
        return Task.Run(() => TranslateAsync(
                text, characterLanguage, _replyLanguage, "speech", CancellationToken.None).AsTask())
            .GetAwaiter().GetResult();
    }

    private async ValueTask<string> TranslateAsync(
        string text,
        string fromLanguage,
        string toLanguage,
        string stage,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var translated = await engine.TranslateAsync(text, fromLanguage, toLanguage, cancellationToken);
        stopwatch.Stop();

        var result = string.IsNullOrWhiteSpace(translated) ? text : translated;
        var elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var charactersPerSecond = elapsedMilliseconds > 0
            ? text.Length / stopwatch.Elapsed.TotalSeconds
            : 0;

        logger.LogInformation(
            "Translator {Stage} {FromLanguage}->{ToLanguage} completed in {ElapsedMilliseconds:F1} ms " +
            "({CharactersPerSecond:F1} source chars/s). Original: {OriginalText} Translated: {TranslatedText}",
            stage,
            fromLanguage,
            toLanguage,
            elapsedMilliseconds,
            charactersPerSecond,
            text,
            result);
        return result;
    }

    private static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
