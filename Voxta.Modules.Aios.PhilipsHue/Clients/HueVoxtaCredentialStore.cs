using System.Text.Json;
using HueApi.Models.Clip;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Encryption;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Aios.PhilipsHue.Clients;

internal sealed class HueVoxtaCredentialStore(IModuleOAuthTokenStore store, ILocalEncryptionProvider encryption,
    Guid userId, Guid moduleId) : IHueCredentialStore
{
    internal const string Field = "HueBridge";
    public async Task<RegisterEntertainmentResult?> LoadAsync(CancellationToken cancellationToken)
    {
        var saved = await store.ListByModuleAsync(userId, moduleId, cancellationToken);
        return saved.TryGetValue(Field, out var token) ? Decode(token) : null;
    }

    public async Task SaveAsync(RegisterEntertainmentResult credentials, CancellationToken cancellationToken)
    {
        Validate(credentials);
        var json = JsonSerializer.Serialize(credentials);
        var token = new AccessTokenInfo(encryption.Encrypt(json), DateTimeOffset.MaxValue, "Philips Hue Bridge");
        var id = await store.SaveAsync(userId, moduleId, Field, null, token, cancellationToken);
        var saved = await store.GetAsync(userId, id, cancellationToken)
            ?? throw new InvalidOperationException("Voxta could not read back the saved Hue credentials.");
        if (JsonSerializer.Serialize(Decode(saved)) != json)
            throw new InvalidOperationException("Voxta could not verify the saved Hue credentials.");
    }

    private RegisterEntertainmentResult Decode(AccessTokenInfo token)
    {
        var credentials = JsonSerializer.Deserialize<RegisterEntertainmentResult>(encryption.Decrypt(token.EncryptedAccessToken))
            ?? throw new InvalidOperationException("The stored Hue credentials are invalid.");
        Validate(credentials);
        return credentials;
    }

    private static void Validate(RegisterEntertainmentResult credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.Ip) || string.IsNullOrWhiteSpace(credentials.Username))
            throw new InvalidOperationException("Hue bridge credentials require a bridge address and application key.");
    }
}
