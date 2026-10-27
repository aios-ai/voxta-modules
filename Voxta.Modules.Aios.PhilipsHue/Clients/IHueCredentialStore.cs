using HueApi.Models.Clip;

namespace Voxta.Modules.Aios.PhilipsHue.Clients;

public interface IHueCredentialStore
{
    Task<RegisterEntertainmentResult?> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(RegisterEntertainmentResult credentials, CancellationToken cancellationToken);
}
