using System.Text.RegularExpressions;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Abstractions.Chats.Objects.Characters;
using Voxta.Abstractions.Scripting.ActionScripts;
using Voxta.Model.Shared;
using Voxta.Model.WebsocketMessages.ClientMessages;
using Voxta.Model.WebsocketMessages.ServerMessages;
using Voxta.Modules.Aios.Spotify.Clients.Handlers;
using Voxta.Modules.Aios.Spotify.Clients.Services;

namespace Voxta.Modules.Aios.Spotify.ChatAugmentations;

public class SpotifyChatAugmentationsServiceInstance(
    IChatSessionChatAugmentationApi session,
    SpotifyChatAugmentationsSettings settings,
    SpotifyPlaybackMonitor spotifyPlaybackMonitor,
    SpotifyActionHandler spotifyActionHandler,
    SpotifySpeechDucking speechDucking
    ) : IActionInferenceAugmentation, IChatScriptEventsAugmentation
{
    public ServiceTypes[] GetRequiredServiceTypes() => [ServiceTypes.ActionInference];
    public string[] GetAugmentationNames() => [VoxtaModule.AugmentationKey];
    private readonly SpotifyPlaybackSession _playback = new(spotifyPlaybackMonitor,
        speechDucking);
    public void Initialize() => _playback.Initialize();

    public IEnumerable<ClientUpdateContextMessage> RegisterChatContext()
    {
        return
        [
            new ClientUpdateContextMessage
            {
                ContextKey = VoxtaModule.ServiceName,
                SessionId = session.SessionId,
                Actions = BuildActions()
            }
        ];
    }

    private ScenarioActionDefinition[] BuildActions()
    {
        return settings.Actions
            .Where(action => !SpotifyChatAugmentationsService.ParseActionBoolean(action.Disabled, false))
            .Select(action => new ScenarioActionDefinition
            {
                Name = action.Name,
                Layer = action.Layer ?? "SpotifyControl",
                ShortDescription = action.ShortDescription ?? "",
                Description = ExpandSpecialPlaylists(action.Description ?? ""),
                MatchFilter = BuildMatchPatterns(action.MatchFilter),
                FlagsFilter = action.FlagsFilter,
                Timing = FunctionTiming.AfterUserMessage,
                CancelReply = SpotifyChatAugmentationsService.ParseActionBoolean(action.CancelReply, true),
                Arguments = GetActionArguments(action.Name),
            })
            .ToArray();
    }

    private string[] BuildMatchPatterns(string? matchFilter)
    {
        return ExpandSpecialPlaylists(matchFilter ?? "")
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(BuildMatchPattern)
            .ToArray();
    }

    private string BuildMatchPattern(string pattern)
    {
        return string.IsNullOrWhiteSpace(settings.MatchFilterWakeWord)
            ? $@"\b(?:{pattern})\b"
            : $@"(?i)(?=.*\b{Regex.Escape(settings.MatchFilterWakeWord)}\b)(?:.*\b(?:{pattern})\b)";
    }

    private string ExpandSpecialPlaylists(string value)
    {
        var playlistNames = string.Join(", ", settings.SpecialPlaylists.Keys);
        var playlistPattern = string.Join("|", settings.SpecialPlaylists.Keys.Select(Regex.Escape));
        return value
            .Replace("{special_playlists}", playlistNames, StringComparison.OrdinalIgnoreCase)
            .Replace("{special_playlists_pattern}", playlistPattern, StringComparison.OrdinalIgnoreCase);
    }

    private FunctionArgumentDefinition[] GetActionArguments(string actionName)
    {
        return actionName switch
        {
            "play_special_playlist" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "name",
                    Description = $"The name of the special playlist the user requested. Options: {string.Join(", ", settings.SpecialPlaylists.Keys)}.",
                    Required = true,
                    Type = FunctionArgumentType.String,
                }
            ],
            "play_music" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "name",
                    Description = "Select all relevant information, like track, album, artist, playlist, show or episode name {{ user }} asked for, so the system can find the right music.",
                    Required = true,
                    Type = FunctionArgumentType.String,
                },
                new FunctionArgumentDefinition
                {
                    Name = "type",
                    Description = "The type of music entity requested: track, album, artist, playlist, show, episode, genre.",
                    Required = true,
                    Type = FunctionArgumentType.String,
                },
            ],
            "queue_track" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "name",
                    Description = "Select all relevant information, like track, album and artist name {{ user }} asked for in the format <artist> <track> <album>, if available so the system can find the right music.",
                    Required = true,
                    Type = FunctionArgumentType.String,
                }
            ],
            "volume" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "type",
                    Description = "The type of volume change: 'set', 'increase', or 'decrease'.",
                    Required = true,
                    Type = FunctionArgumentType.String,
                },
                new FunctionArgumentDefinition
                {
                    Name = "value",
                    Description = "The value for the volume change (e.g., percentage for 'set', percentage points for 'increase'/'decrease'). Optional for 'increase'/'decrease' (defaults to 10).",
                    Required = false,
                    Type = FunctionArgumentType.Integer,
                }
            ],
            "seek_playback" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "target",
                    Description = "The type of seek: 'forward'/'backward' for relative seconds, 'forward_percent'/'backward_percent' for relative percentage of total duration from the current position, 'to_time'/'to_percent' for absolute positions from the beginning, or 'middle'.",
                    Required = true,
                    Type = FunctionArgumentType.String,
                },
                new FunctionArgumentDefinition
                {
                    Name = "value",
                    Description = "The value for the target (e.g., seconds for 'forward'/'backward', total seconds for 'to_time', 0-100 percentage of total duration for 'forward_percent'/'backward_percent'/'to_percent'). Not needed for 'middle'.",
                    Required = false,
                    Type = FunctionArgumentType.Integer,
                }
            ],
            "repeat_mode" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "mode",
                    Description = "it must be one of those: [track, context, off]",
                    Required = true,
                    Type = FunctionArgumentType.String,
                }
            ],
            "shuffle_mode" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "mode",
                    Description = "Select on or off based on {{ user }}s requested",
                    Required = true,
                    Type = FunctionArgumentType.String,
                }
            ],
            "add_to_playlist" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "playlist",
                    Description = "Select the playlist name based {{ user }} requested",
                    Required = true,
                    Type = FunctionArgumentType.String,
                }
            ],
            "transfer_to_device" =>
            [
                new FunctionArgumentDefinition
                {
                    Name = "device",
                    Description = "Select the device name based {{ user }} requested",
                    Required = true,
                    Type = FunctionArgumentType.String,
                }
            ],
            _ => [],
        };
    }
    
    public async ValueTask<bool> TryHandleActionInference(
        ChatMessageData? message,
        ServerActionMessage serverActionMessage,
        CancellationToken cancellationToken
    )
    {
        if (serverActionMessage.ContextKey != VoxtaModule.ServiceName)
            return false;
        await spotifyActionHandler.HandleAction(serverActionMessage, cancellationToken);
        return true;
    }
    
    public Task OnChatScriptEvent(IActionScriptEvent e, ChatMessageData? message, ICharacterOrUserData? character, CancellationToken cancellationToken) =>
        _playback.OnChatScriptEvent(e, message, character, cancellationToken);

    public ValueTask DisposeAsync() => _playback.DisposeAsync();
}
