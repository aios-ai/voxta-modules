using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Abstractions.Testing;
using Voxta.Modules.Aios.Spotify.Clients.Services;
using Voxta.Modules.Aios.Spotify.Configuration;

namespace Voxta.Modules.Aios.Spotify;

public class ModuleTestingProvider(
    ILocalEncryptionProvider localEncryptionProvider,
    ISpotifyManagerFactory spotifyManagerFactory,
    ILogger<ModuleTestingProvider> logger
    ) : IVoxtaModuleTestingProvider
{
    public async Task<ModuleTestResultItem[]> TestModuleAsync(
        IAuthenticationContext auth,
        Guid moduleId,
        ISettingsSource settings,
        CancellationToken cancellationToken
        )
    {
        var config = new SpotifyManagerConfig
        {
            UserId = auth.UserId,
            TokenId = Guid.TryParse(settings.GetOptional(ModuleConfigurationProvider.SpotifyAccount), out var tokenId) ? tokenId : null,
            ClientId = settings.GetRequired(ModuleConfigurationProvider.ClientId),
            ClientSecret = localEncryptionProvider.Decrypt(settings.GetRequired(ModuleConfigurationProvider.ClientSecret)),
            Abort = cancellationToken,
        };
        var client = await spotifyManagerFactory.CreateSpotifyManager(config, cancellationToken);
        try
        {
            var spotifyUserId = await client.GetSpotifyUserIdAsync(cancellationToken);
            if (spotifyUserId == null)
            {
                return
                [
                    new ModuleTestResultItem
                    {
                        Success = false,
                        Message = client.LastUserVisibleError ?? "Spotify authorization was not completed.",
                    }
                ];
            }
            return
            [
                new ModuleTestResultItem
                {
                    Success = true,
                    Message = $"Successfully connect to Spotify as user {spotifyUserId}",
                }
            ];
        }
        catch (Exception exc)
        {
            logger.LogError(exc, "Failed to connect to Spotify");
            return
            [
                new ModuleTestResultItem
                {
                    Success = false,
                    Message = "Failed to connect to Spotify: " + exc.Message,
                }
            ];
        }

    }
}
