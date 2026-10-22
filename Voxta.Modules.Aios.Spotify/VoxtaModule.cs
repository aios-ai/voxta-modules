using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Model.Shared;
using Voxta.Modules.Aios.Spotify.ChatAugmentations;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Configuration;
using Voxta.Modules.Aios.Spotify.Controllers;

namespace Voxta.Modules.Aios.Spotify;

public class VoxtaModule : IVoxtaModule
{
    public const string ServiceName = "Aios.Spotify";
    public const string AugmentationKey = "spotify";

    public static readonly AugmentationKeyDescriptor[] Augmentations =
    [
        new(
            AugmentationKey,
            "Spotify Control",
            "Control Spotify playback, search, playlists, queues, volume, and devices from a chat."
        ),
    ];

    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new()
        {
            ServiceName = ServiceName,
            Label = "Spotify Control (by aios)",
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
            Notes = "Control Spotify playback, search, playlists, queues, volume, and devices from Voxta chats. The module is free; a Spotify Premium account and Spotify Developer application are required.",
            HelpLink = "https://github.com/aios-ai/voxta-modules",
            Logo = ModuleLogo.EmbeddedResource(typeof(VoxtaModule), "Assets.spotify.png"),
            Augmentations = Augmentations,
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
            ModuleConfigurationFieldsRequiringReload = ModuleConfigurationProvider.FieldsRequiringReload,
            ModuleTestingProviderType = typeof(ModuleTestingProvider),
        });

        builder.AddChatAugmentationsService<SpotifyChatAugmentationsService>(ServiceName);

        builder.Services.AddSingleton<ISpotifyManagerFactory, SpotifyManagerFactory>();
        builder.Services.AddSingleton<ISpotifyAuthCallbackManager, SpotifyAuthCallbackManager>();

        builder.Services.AddControllers().PartManager.ApplicationParts.Add(new AssemblyPart(typeof(SpotifyController).Assembly));
    }
}
