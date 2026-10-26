using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Encryption;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.Spotify.Clients.Models;

namespace Voxta.Modules.Aios.Spotify.Clients.Services;

public interface ISpotifyTokenStore
{
    Task<SpotifyAuthToken?> LoadAsync(CancellationToken token);
    Task SaveAsync(SpotifyAuthToken value, CancellationToken token);
    Task DeleteAsync(CancellationToken token);
}

/// <summary>Adapt Voxta's per-user encrypted account store to the Spotify manager.</summary>
internal sealed class SpotifyVoxtaTokenStore(IModuleOAuthTokenStore store, ILocalEncryptionProvider encryption,
    Guid userId, Guid? tokenId) : ISpotifyTokenStore
{
    public async Task<SpotifyAuthToken?> LoadAsync(CancellationToken token)
    {
        if (tokenId == null) return null;
        var value = await store.GetAsync(userId, tokenId.Value, token);
        return value == null ? null : new SpotifyAuthToken
        {
            AccessToken = encryption.Decrypt(value.EncryptedAccessToken),
            RefreshToken = value.EncryptedRefreshToken == null ? null : encryption.Decrypt(value.EncryptedRefreshToken),
            ExpiresAt = value.Expires.UtcDateTime,
        };
    }

    public async Task SaveAsync(SpotifyAuthToken value, CancellationToken token)
    {
        if (tokenId == null) throw new InvalidOperationException("Authorize the Spotify Account field in module settings first.");
        var previous = await store.GetAsync(userId, tokenId.Value, token)
            ?? throw new InvalidOperationException("Spotify authorization was removed. Authorize Spotify Account again.");
        var updated = previous with
        {
            EncryptedAccessToken = encryption.Encrypt(value.AccessToken!),
            EncryptedRefreshToken = value.RefreshToken == null ? null : encryption.Encrypt(value.RefreshToken),
            Expires = new DateTimeOffset(value.ExpiresAt.ToUniversalTime()),
        };
        if (!await store.UpdateAsync(userId, tokenId.Value, updated, token))
            throw new InvalidOperationException("Spotify authorization was removed. Authorize Spotify Account again.");
    }

    public Task DeleteAsync(CancellationToken token) => tokenId == null ? Task.CompletedTask : store.DeleteAsync(userId, tokenId.Value, token);
}
