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
    ILogger logger)
    : IChatInboundTextAugmentation, IChatPostProcessAugmentation
{
    private readonly string _userLanguage = NormalizeLanguage(userLanguage);
    private readonly string _replyLanguage = NormalizeLanguage(replyLanguage);

    public ServiceTypes[] GetRequiredServiceTypes() => [];

    private string CharacterLanguage => NormalizeLanguage(
        session.MainCharacter?.Culture?.TwoLetterISOLanguageName ?? "en");

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
        if (role != ChatMessageRole.Assistant ||
            string.IsNullOrWhiteSpace(text) ||
            string.Equals(CharacterLanguage, _replyLanguage, StringComparison.Ordinal))
        {
            return text;
        }

        return await TranslateAsync(text, CharacterLanguage, _replyLanguage, "reply", cancellationToken);
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
        logger.LogDebug(
            "Translator {Stage} completed {FromLanguage}->{ToLanguage} in {ElapsedMilliseconds} ms",
            stage,
            fromLanguage,
            toLanguage,
            stopwatch.ElapsedMilliseconds);
        return string.IsNullOrWhiteSpace(translated) ? text : translated;
    }

    private static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
