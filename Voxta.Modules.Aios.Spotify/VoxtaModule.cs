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
            "Spotify",
            "Control Spotify playback, search, playlists, and devices from a chat."
        ),
    ];
    
    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new()
        {
            ServiceName = ServiceName,
            Label = "Spotify (by aios)",
            Version = ModuleInfo.Version,
            Experimental = true,
            InstallableBy = ModuleInstallableBy.Anyone,
            Supports = new()
            {
                ChatAugmentations = new(ServiceDefinitionCategoryScore.Medium),
            },
            Pricing = ServiceDefinitionPricing.Medium,
            Hosting = ServiceDefinitionHosting.Online,
            SupportsExplicitContent = true,
            Recommendation = ServiceDefinitionRecommendation.Recommended,
            Notes = "Spotify augmentations.",
            HelpLink = "https://github.com/aios-ai/voxta-spotify-module",
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
