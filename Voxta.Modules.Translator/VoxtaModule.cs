using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Utils;
using Voxta.Modules.Translator.ChatAugmentations;
using Voxta.Modules.Translator.Configuration;
using Voxta.Modules.Translator.Translation;

namespace Voxta.Modules.Translator;

public sealed class VoxtaModule : IVoxtaModule
{
    public const string ServiceName = "Translator";
    public const string AugmentationKey = "translator";
    private const string HttpClientName = "TranslatorModels";

    public static readonly AugmentationKeyDescriptor[] Augmentations =
    [
        new(AugmentationKey, "Translator", "Translates user messages and character replies with local Bergamot models."),
    ];

    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new()
        {
            ServiceName = ServiceName,
            Label = "Translator",
            Version = ModuleInfo.Version,
            Flags = [ModuleFlag.Experimental, ModuleFlag.SupportsExplicitContent],
            Supports = new()
            {
                ChatAugmentations = new(ServiceDefinitionCategoryScore.Medium),
            },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Builtin,
            Notes = "Local translation. Selected language models are downloaded on first use.",
            Augmentations = Augmentations,
            Logo = ModuleLogo.EmbeddedResource(typeof(VoxtaModule), "Assets.logo.svg"),
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
            ModuleConfigurationFieldsRequiringReload = ModuleConfigurationProvider.FieldsRequiringReload,
        });

        builder.Services.AddHttpClient(HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Voxta.Modules.Translator/" + ModuleInfo.Version);
        });
        builder.Services.AddSingleton(sp => new ModelStore(
            sp.GetRequiredService<ICommonFolders>().GetDataFolder(ServiceName),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            sp.GetRequiredService<ILogger<ModelStore>>()));
        builder.Services.AddSingleton<BergamotTranslationEngine>();
        builder.Services.AddSingleton<ITranslationEngine>(sp => sp.GetRequiredService<BergamotTranslationEngine>());
        builder.Services.AddSingleton<TranslatorEngineRegistry>();

        builder.AddChatAugmentationsService<TranslatorAugmentationsService>(ServiceName);
    }
}
