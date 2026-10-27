using SpotifyAPI.Web;

namespace Voxta.Modules.Aios.Spotify.Clients.Services;

public enum SpotifyDuckingMode { Fixed, Relative }

/// <summary>Session playback volume, independent of action inference and tool feedback.</summary>
public sealed class SpotifySpeechDucking(ISpotifyManager manager, Func<CurrentlyPlayingContext?> getState, int amount, SpotifyDuckingMode mode = SpotifyDuckingMode.Fixed)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    private int? _restoreVolume;

    public async Task LowerVolumeAsync(string source, CancellationToken token)
    {
        if (mode == SpotifyDuckingMode.Fixed ? amount >= 100 : amount <= 0) return;
        await _gate.WaitAsync(token);
        try
        {
            if (!_sources.Add(source) || _restoreVolume.HasValue) return;
            var device = getState()?.Device;
            if (device?.Id == null || device.VolumePercent is not { } volume) return;
            var effective = DuckedVolume(volume);
            if (effective >= volume) return;
            if (await manager.ChangeVolume(effective, token)) _restoreVolume = volume;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreVolumeAsync(string source, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            _sources.Remove(source);
            if (_sources.Count == 0 && _restoreVolume is { } volume && await manager.ChangeVolume(volume, token))
                _restoreVolume = null;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ChangeVolumeAsync(string type, int value, CancellationToken token, int? currentVolume = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            var current = _restoreVolume ?? currentVolume ?? getState()?.Device?.VolumePercent ?? 50;
            var volume = Math.Clamp(type switch { "increase" => current + value, "decrease" => current - value, _ => value }, 0, 100);
            var effective = _sources.Count > 0 ? DuckedVolume(volume) : volume;
            if (!await manager.ChangeVolume(effective, token)) return false;
            _restoreVolume = effective == volume ? null : volume;
            return true;
        }
        finally { _gate.Release(); }
    }
    private int DuckedVolume(int volume) => mode == SpotifyDuckingMode.Relative
        ? (int)Math.Round(volume * (100 - Math.Clamp(amount, 0, 100)) / 100.0, MidpointRounding.AwayFromZero)
        : Math.Min(volume, Math.Clamp(amount, 0, 100));

}
