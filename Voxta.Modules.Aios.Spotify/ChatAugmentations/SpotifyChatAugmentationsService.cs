using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Configuration;
using Voxta.Modules.Aios.Spotify.Helpers;
using Voxta.Modules.Aios.Spotify.Tools;

namespace Voxta.Modules.Aios.Spotify.ChatAugmentations;

public class SpotifyChatAugmentationsService(
    ILocalEncryptionProvider localEncryptionProvider,
    ISpotifyManagerFactory spotifyManagerFactory,
    ILoggerFactory loggerFactory
) : ServiceBase(loggerFactory.CreateLogger<SpotifyChatAugmentationsService>()), IToolsChatAugmentationsService
{
    public Task<AugmentationPlanItem[]> PlanAsync(
        IChatAugmentationPlanContext context,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult<AugmentationPlanItem[]>(
        [
            CreateToolsPlan(),
        ]);
    }
    
    private AugmentationPlanItem CreateToolsPlan()
    {
        var plan = VoxtaModule.Augmentations.Get(VoxtaModule.ToolsAugmentationKey).Plan(
            AugmentationActivation.OptIn, CreateInstance);
        return new AugmentationPlanItem
        {
            Key = plan.Key, Label = plan.Label, Description = plan.Description,
            Activation = plan.Activation, ActivateAsync = plan.ActivateAsync,
            IncludeInCatchAll = false,
            Tools = SpotifyToolCatalog.Choices.Select(choice => new AugmentationToolDeclaration(choice.Name, VoxtaModule.ServiceName)).ToArray(),
        };
    }

    private async Task<IChatAugmentationServiceInstanceBase?> CreateInstance(IChatSessionChatAugmentationApi session, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<SpotifyToolsSession>();
        logger.LogInformation("Chat session {SessionId} has been augmented with {Augmentation}", session.SessionId, VoxtaModule.ToolsAugmentationKey);
        
        var rawPlaylists = ModuleConfiguration.GetOptional(ModuleConfigurationProvider.SpecialPlaylists) ?? "";
        var playlistMap = rawPlaylists
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
            .ToDictionary(
                parts => StringUtils.NormaliseSpecialName(parts[0]),
                parts => parts[1]
            );
        
        var spotifyManagerConfig = new SpotifyManagerConfig
        {
            UserId = Auth.UserId,
            TokenId = Guid.TryParse(ModuleConfiguration.GetOptional(ModuleConfigurationProvider.SpotifyAccount), out var tokenId) ? tokenId : null,
            ClientId = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ClientId),
            ClientSecret = localEncryptionProvider.Decrypt(ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ClientSecret)),
            Abort = session.Chat.Abort,
        };
        var spotifyManager = await spotifyManagerFactory.CreateSpotifyManager(spotifyManagerConfig, cancellationToken);

        var spotifyPlaybackMonitor = new SpotifyPlaybackMonitor(spotifyManager, session, loggerFactory.CreateLogger<SpotifyPlaybackMonitor>());
        
        var ducking = new SpotifySpeechDucking(spotifyManager, () => spotifyPlaybackMonitor.PlaybackState, ModuleConfiguration.GetRequired(ModuleConfigurationProvider.SpeechDuckingVolumePercent));
        var playback = new SpotifyPlaybackSession(spotifyPlaybackMonitor, ducking);
        var handler = new SpotifyToolHandler(spotifyManager, playlistMap, ducking);
        var instance = new SpotifyToolsSession(SpotifyToolCatalog.Create(handler, SpotifyToolsSettings.From(ModuleConfiguration), playlistMap, logger), playback);
        try { await playback.InitializeAsync(cancellationToken); }
        catch { await instance.DisposeAsync(); throw; }
        return instance;
    }
}
