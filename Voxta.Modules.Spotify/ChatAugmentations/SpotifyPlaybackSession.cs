using Voxta.Abstractions.Chats.Objects.Characters;
using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Scripting.ActionScripts;
using Voxta.Abstractions.Services.ChatAugmentations;
using Voxta.Model.Shared;
using Voxta.Modules.Spotify.Clients.Services;

namespace Voxta.Modules.Spotify.ChatAugmentations;

internal sealed class SpotifyPlaybackSession(SpotifyPlaybackMonitor monitor, SpotifySpeechDucking ducking, IChatSessionChatAugmentationApi session)
    : IChatScriptEventsAugmentation
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitorTask;
    private bool _disposed;
    public bool HasActivePlayer => monitor.PlaybackState?.Device?.IsActive == true;
    public ServiceTypes[] GetRequiredServiceTypes() => [];
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await monitor.RefreshAsync(linked.Token);
        _monitorTask = monitor.MonitorSpotifyPlayback(_cts.Token, false);
    }

    public async Task OnChatScriptEvent(IActionScriptEvent e, ChatMessageData? message, ICharacterOrUserData? character, CancellationToken cancellationToken)
    {
        switch (e)
        {
            case TranscriptionStartedScriptEvent: await ducking.LowerVolumeAsync("transcription", cancellationToken); break;
            case TranscriptionFinishedScriptEvent: await ducking.RestoreVolumeAsync("transcription", cancellationToken); break;
            case SpeechStartActionScriptEvent when session.Chat.ChatSettings.SpeakerMuted != true:
                await ducking.LowerVolumeAsync("speech", cancellationToken);
                break;
            case SpeechCompleteActionScriptEvent: await ducking.RestoreVolumeAsync("speech", cancellationToken); break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _cts.CancelAsync();
        try { if (_monitorTask != null) await _monitorTask; }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        finally { _cts.Dispose(); }
    }
}
