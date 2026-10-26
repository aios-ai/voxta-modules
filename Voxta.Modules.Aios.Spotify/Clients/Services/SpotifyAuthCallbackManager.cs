namespace Voxta.Modules.Aios.Spotify.Clients.Services;

public interface ISpotifyAuthCallbackManager
{
    SpotifyAuthCallbackResult Callback(string code, string state);
    SpotifyAuthCallbackResult Reject(string state);
    Task<string> WaitForCodeAsync(string state, CancellationToken cancellationToken);
    void Release(string state);
}

public enum SpotifyAuthCallbackResult
{
    Accepted,
    NoPendingAuthorization,
    InvalidState,
}

public class SpotifyAuthCallbackManager : ISpotifyAuthCallbackManager
{
    private readonly Lock _lock = new();
    private PendingAuthorization? _pendingAuthorization;

    public SpotifyAuthCallbackResult Reject(string state)
    {
        lock (_lock)
        {
            if (_pendingAuthorization == null) return SpotifyAuthCallbackResult.NoPendingAuthorization;
            if (!string.Equals(_pendingAuthorization.State, state, StringComparison.Ordinal)) return SpotifyAuthCallbackResult.InvalidState;
            _pendingAuthorization.Code.TrySetException(new SpotifyAuthorizationDeniedException());
            _pendingAuthorization = null;
            return SpotifyAuthCallbackResult.Accepted;
        }
    }
    
    public SpotifyAuthCallbackResult Callback(string code, string state)
    {
        lock (_lock)
        {
            if (_pendingAuthorization == null)
                return SpotifyAuthCallbackResult.NoPendingAuthorization;

            if (!string.Equals(_pendingAuthorization.State, state, StringComparison.Ordinal))
                return SpotifyAuthCallbackResult.InvalidState;

            _pendingAuthorization.Code.TrySetResult(code);
            _pendingAuthorization = null;
            return SpotifyAuthCallbackResult.Accepted;
        }
    }

    public Task<string> WaitForCodeAsync(string state, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_pendingAuthorization != null)
                throw new InvalidOperationException("Another Spotify authorization request is already waiting for a callback.");

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAuthorization = new PendingAuthorization(state, tcs);
            cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    if (_pendingAuthorization?.Code != tcs)
                        return;

                    _pendingAuthorization = null;
                    tcs.TrySetCanceled(cancellationToken);
                }
            });
            return tcs.Task;
        }
    }
    
    public void Release(string state)
    {
        lock (_lock)
        {
            if (_pendingAuthorization == null ||
                !string.Equals(_pendingAuthorization.State, state, StringComparison.Ordinal))
                return;

            _pendingAuthorization.Code.TrySetCanceled();
            _pendingAuthorization = null;
        }
    }

    private sealed record PendingAuthorization(string State, TaskCompletionSource<string> Code);
}

internal sealed class SpotifyAuthorizationDeniedException : Exception;
