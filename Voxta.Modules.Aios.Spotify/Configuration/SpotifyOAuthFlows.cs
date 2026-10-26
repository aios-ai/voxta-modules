namespace Voxta.Modules.Aios.Spotify.Configuration;

/// <summary>
/// The SDK supplies returnUrl only when starting OAuth. Retain it until the code exchange,
/// isolated by authenticated user and module instance; never guess or retry redirect URIs.
/// </summary>
public sealed class SpotifyOAuthFlows(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid User, Guid Module), Pending> _pending = new();
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    internal void Begin(Guid user, Guid module, string credentials, Uri redirect)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            foreach (var key in _pending.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray())
                _pending.Remove(key);
            var keyForFlow = (user, module);
            if (_pending.TryGetValue(keyForFlow, out var existing) && existing.Redirect != redirect)
                throw new InvalidOperationException("A Spotify sign-in is already pending in another client for this module. Finish it first, or wait five minutes before starting again here.");
            _pending[keyForFlow] = new(credentials, redirect, now + Lifetime);
        }
    }

    internal Uri Consume(Guid user, Guid module, string credentials)
    {
        lock (_lock)
        {
            if (!_pending.Remove((user, module), out var flow) || flow.Expires <= _clock.GetUtcNow())
                throw new InvalidOperationException("This Spotify sign-in is no longer active. Start sign-in again from Spotify Account.");
            if (!string.Equals(flow.Credentials, credentials, StringComparison.Ordinal))
                throw new InvalidOperationException("Spotify client credentials changed during sign-in. Save the settings and start sign-in again.");
            return flow.Redirect;
        }
    }

    internal void Cancel(Guid user, Guid module)
    {
        lock (_lock) _pending.Remove((user, module));
    }

    private sealed record Pending(string Credentials, Uri Redirect, DateTimeOffset Expires);
}
