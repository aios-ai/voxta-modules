using System.Collections.Concurrent;
using System.Text.Json;
using HueApi.Models.Clip;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Encryption;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Aios.PhilipsHue.Clients;

internal sealed class HueVoxtaCredentialStore(IModuleOAuthTokenStore store, ILocalEncryptionProvider encryption,
    Guid userId, Guid moduleId, Func<string> legacyPath) : IHueCredentialStore
{
    internal const string Field = "HueBridge";
    private static readonly ConcurrentDictionary<(Guid User, Guid Module), SemaphoreSlim> Gates = new();
    private readonly SemaphoreSlim _gate = Gates.GetOrAdd((userId, moduleId), _ => new(1, 1));

    public async Task<RegisterEntertainmentResult?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var saved = await store.ListByModuleAsync(userId, moduleId, cancellationToken);
            if (saved.TryGetValue(Field, out var token)) return Decode(token);
            var path = legacyPath();
            if (!File.Exists(path)) return null;
            var credentials = JsonSerializer.Deserialize<RegisterEntertainmentResult>(await File.ReadAllTextAsync(path, cancellationToken))
                ?? throw new InvalidOperationException("The legacy Hue credentials are invalid.");
            Validate(credentials);
            // Import only after storage is available. SaveAsync verifies a full encrypted-store round trip.
            await SaveCoreAsync(credentials, cancellationToken);
            // The old key is removed only after the durable copy has been read back successfully.
            File.Delete(path);
            return credentials;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(RegisterEntertainmentResult credentials, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await SaveCoreAsync(credentials, cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task SaveCoreAsync(RegisterEntertainmentResult credentials, CancellationToken cancellationToken)
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
