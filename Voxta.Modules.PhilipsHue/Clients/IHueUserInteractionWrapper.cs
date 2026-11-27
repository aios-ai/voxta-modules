using Voxta.Abstractions.Utils;

namespace Voxta.Modules.PhilipsHue.Clients;

public interface IHueUserInteractionWrapper
{
    Task<IUserInteractionRequestToken> RequestUserInteraction(CancellationToken cancellationToken);
}
