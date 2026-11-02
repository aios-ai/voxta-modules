using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Translator.Translation;
using Microsoft.Extensions.Logging;

namespace Voxta.Modules.Translator.Configuration;

public sealed class ModuleConfigurationProvider(
    TranslatorEngineRegistry registry,
    ILogger<ModuleConfigurationProvider> logger)
    : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public const string EngineName = "Engine";
    public const string UserLanguageName = "UserLanguage";
    public const string ChatLanguageName = "ChatLanguage";

    public static string[] FieldsRequiringReload => [];

    public static readonly FormChoicesField Engine = new()
    {
        Name = EngineName,
        Label = "Translation engine",
        DefaultValue = BergamotTranslationEngine.EngineId,
        Required = true,
        Help = "The local translation engine. Additional engines can be added in future releases.",
        Choices =
        [
            new FormChoice { Value = BergamotTranslationEngine.EngineId, Label = "Bergamot (local)" },
        ],
    };

    public static readonly FormChoicesField UserLanguage = LanguageField(
        UserLanguageName,
        "Your language",
        "Messages you send are translated from this language into the character's configured language.");

    public static readonly FormChoicesField ChatLanguage = LanguageField(
        ChatLanguageName,
        "Reply language",
        "Character replies are translated from the character's configured language into this language.");

    public async Task<FormField[]> GetModuleConfigurationFieldsAsync(
        IAuthenticationContext auth,
        ISettingsSource settings,
        CancellationToken cancellationToken)
    {
        FormChoice[] choices;
        try
        {
            var languages = await registry.GetRequired(BergamotTranslationEngine.EngineId)
                .GetSupportedLanguagesAsync(cancellationToken);
            choices = languages
                .Select(language => new FormChoice { Value = language.Code, Label = language.Label })
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not load the supported translation languages");
            choices = [new FormChoice { Value = "en", Label = "English (en)" }];
        }

        var userLanguage = LanguageField(
            UserLanguageName,
            "Your language",
            "Messages you send are translated from this language into the character's configured language.");
        userLanguage.Choices = choices;
        var replyLanguage = LanguageField(
            ChatLanguageName,
            "Reply language",
            "Character replies are translated from the character's configured language into this language.");
        replyLanguage.Choices = choices;

        return FormBuilder.Build(
            FormTitleField.Create("Translator"),
            Engine,
            userLanguage,
            replyLanguage);
    }

    private static FormChoicesField LanguageField(string name, string label, string help) => new()
    {
        Name = name,
        Label = label,
        DefaultValue = "en",
        Required = true,
        Help = help,
        Choices = [new FormChoice { Value = "en", Label = "English (en)" }],
    };
}
