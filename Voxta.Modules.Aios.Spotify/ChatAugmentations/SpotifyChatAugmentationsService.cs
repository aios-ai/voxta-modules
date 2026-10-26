using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Modules.Aios.Spotify.Clients.Handlers;
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
            VoxtaModule.Augmentations.Get(VoxtaModule.AugmentationKey).Plan(
                AugmentationActivation.OptIn,
                CreateLegacyInstance
            ),
            CreateToolsPlan(),
        ]);
    }
    
    private AugmentationPlanItem CreateToolsPlan()
    {
        var plan = VoxtaModule.Augmentations.Get(VoxtaModule.ToolsAugmentationKey).Plan(
            AugmentationActivation.OptIn, (session, token) => CreateInstance(session, true, token));
        return new AugmentationPlanItem
        {
            Key = plan.Key, Label = plan.Label, Description = plan.Description,
            Activation = plan.Activation, ActivateAsync = plan.ActivateAsync,
            IncludeInCatchAll = false,
            Tools = SpotifyToolCatalog.Choices.Select(choice => new AugmentationToolDeclaration(choice.Name, VoxtaModule.ServiceName)).ToArray(),
        };
    }

    private Task<IChatAugmentationServiceInstanceBase?> CreateLegacyInstance(IChatSessionChatAugmentationApi session, CancellationToken token) =>
        session.IsAugmentationEnabled(VoxtaModule.ToolsAugmentationKey, false) && !session.IsAugmentationSuppressed(VoxtaModule.ToolsAugmentationKey)
            ? Task.FromResult<IChatAugmentationServiceInstanceBase?>(null)
            : CreateInstance(session, false, token);

    private async Task<IChatAugmentationServiceInstanceBase?> CreateInstance(IChatSessionChatAugmentationApi session, bool tools, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger<SpotifyChatAugmentationsServiceInstance>();
        logger.LogInformation("Chat session {SessionId} has been augmented with {Augmentation}", session.SessionId, tools ? VoxtaModule.ToolsAugmentationKey : VoxtaModule.AugmentationKey);
        
        var rawPlaylists = ModuleConfiguration.GetOptional(ModuleConfigurationProvider.SpecialPlaylists) ?? "";
        var playlistMap = rawPlaylists
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[1]))
            .ToDictionary(
                parts => StringUtils.NormaliseSpecialName(parts[0]),
                parts => parts[1]
            );
        
        var config = new SpotifyChatAugmentationsSettings
        {
            Actions = tools ? [] : NormalizeActions(ModuleConfiguration.GetOptional<SpotifyActionSettings>(ModuleConfigurationProvider.Actions)),
            MatchFilterWakeWord = tools ? null : ModuleConfiguration.GetOptional(ModuleConfigurationProvider.MatchFilterWakeWord),
            SpeechDuckingVolumePercent = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.SpeechDuckingVolumePercent),
            SpecialPlaylists = playlistMap
        };

        var tokenPath = SpotifyAuthPath.GetUserTokenPath(ModuleConfiguration.GetRequired(ModuleConfigurationProvider.TokenPath), Auth.UserId);
        var spotifyManagerConfig = new SpotifyManagerConfig
        {
            ClientId = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ClientId),
            ClientSecret = localEncryptionProvider.Decrypt(ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ClientSecret)),
            RedirectUri = new Uri(ModuleConfiguration.GetRequired(ModuleConfigurationProvider.RedirectUri)),
            TokenPath = tokenPath,
        };
        var sessionWrapper = new SpotifyUserInteractionWrapper(session, InstanceSettings.ModuleId);
        var spotifyManager = await spotifyManagerFactory.CreateSpotifyManager(sessionWrapper, spotifyManagerConfig, cancellationToken);

        var spotifySearchService = new SpotifySearchService(spotifyManager, loggerFactory.CreateLogger<SpotifySearchService>());
        await spotifySearchService.InitializeAsync(cancellationToken);

        var spotifyPlaybackMonitor = new SpotifyPlaybackMonitor(spotifyManager, session, loggerFactory.CreateLogger<SpotifyPlaybackMonitor>());
        
        var ducking = new SpotifySpeechDucking(spotifyManager, () => spotifyPlaybackMonitor.PlaybackState, config.SpeechDuckingVolumePercent);
        if (tools)
        {
            var playback = new SpotifyPlaybackSession(spotifyPlaybackMonitor, ducking);
            var handler = new SpotifyToolHandler(spotifyManager, spotifySearchService, playlistMap, ducking, spotifyPlaybackMonitor.SetLastActionAsync);
            var toolInstance = new SpotifyToolsSession(SpotifyToolCatalog.Create(handler, SpotifyToolsSettings.From(ModuleConfiguration), playlistMap, logger), playback);
            try { playback.Initialize(); }
            catch { await toolInstance.DisposeAsync(); throw; }
            return toolInstance;
        }

        var spotifyActionHandler = new SpotifyActionHandler(spotifyManager, spotifySearchService, session, config,
            loggerFactory.CreateLogger<SpotifyActionHandler>(), () => spotifyPlaybackMonitor.PlaybackState, spotifyPlaybackMonitor.SetLastActionAsync, ducking);
        var instance = new SpotifyChatAugmentationsServiceInstance(session, config, spotifyPlaybackMonitor, spotifyActionHandler, ducking);
        try { instance.Initialize(); }
        catch { await instance.DisposeAsync(); throw; }
        return instance;
    }

    private static SpotifyActionSettings[] NormalizeActions(SpotifyActionSettings[]? actions)
    {
        if (actions is not { Length: > 0 })
            return ModuleConfigurationProvider.DefaultActions;

        var defaultsByName = ModuleConfigurationProvider.DefaultActions.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        return actions
            .Where(action => !string.IsNullOrWhiteSpace(action.Name) && defaultsByName.ContainsKey(action.Name))
            .Select(action =>
            {
                var defaultAction = defaultsByName[action.Name];
                return new SpotifyActionSettings
                {
                    Name = defaultAction.Name,
                    Layer = defaultAction.Layer,
                    ShortDescription = string.IsNullOrWhiteSpace(action.ShortDescription) ? defaultAction.ShortDescription : action.ShortDescription,
                    Description = string.IsNullOrWhiteSpace(action.Description) ? defaultAction.Description : action.Description,
                    MatchFilter = NormalizeMultilineSetting(action.MatchFilter, defaultAction.MatchFilter),
                    FlagsFilter = string.IsNullOrWhiteSpace(action.FlagsFilter) ? defaultAction.FlagsFilter : action.FlagsFilter,
                    Disabled = action.Disabled,
                    CancelReply = action.CancelReply,
                };
            })
            .ToArray();
    }

    private static string? NormalizeMultilineSetting(string? value, string? fallback)
    {
        var values = (value ?? "")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        return values.Length == 0 ? fallback : string.Join(Environment.NewLine, values);
    }

    public static bool ParseActionBoolean(string? value, bool fallback)
    {
        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
