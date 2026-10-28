using Microsoft.Extensions.DependencyInjection;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.OpenWeather.ChatAugmentations;
using Voxta.Modules.Aios.OpenWeather.Clients;
using Voxta.Modules.Aios.OpenWeather.Configuration;

namespace Voxta.Modules.Aios.OpenWeather;

public class VoxtaModule : IVoxtaModule
{
    public const string ServiceName = "Aios.OpenWeather";
    public const string AugmentationKey = "openweather";
    public const string ToolsAugmentationKey = "openweather_tools";

    public static readonly AugmentationKeyDescriptor[] Augmentations =
    [
        new(ToolsAugmentationKey, "OpenWeather Tools", "Query weather, forecasts, air quality and maps through native chat tools."),
        new(
            AugmentationKey,
            "OpenWeather",
            "Add current weather, forecasts, air quality, and weather maps to chats."
        ),
    ];

    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new()
        {
            ServiceName = ServiceName,
            Label = "OpenWeather",
            Version = ModuleInfo.Version,
            Category = "Augmentations",
            Flags = [ModuleFlag.Experimental],
            InstallableBy = ModuleInstallableBy.Anyone,
            Supports = new()
            {
                ChatAugmentations = new(ServiceDefinitionCategoryScore.Medium),
            },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Online,
            Notes = "Add current weather, forecasts, air quality, and weather maps to Voxta chats. The module is free and requires an OpenWeather API key; OpenWeather offers a rate-limited free plan.",
            HelpLink = "https://github.com/aios-ai/voxta-modules",
            Logo = ModuleLogo.EmbeddedResource(typeof(VoxtaModule), "Assets.OpenWeather.png"),
            Augmentations = Augmentations,
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
            ModuleConfigurationFieldsRequiringReload = ModuleConfigurationProvider.FieldsRequiringReload,
            ModuleTestingProviderType = typeof(ModuleTestingProvider),
        });

        builder.AddChatAugmentationsService<OpenWeatherChatAugmentationsService>(ServiceName);

        builder.Services.AddSingleton<IOpenWeatherClientFactory, OpenWeatherClientFactory>();
    }
}
