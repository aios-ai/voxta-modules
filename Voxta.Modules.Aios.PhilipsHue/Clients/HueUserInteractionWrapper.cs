using Voxta.Abstractions.Chats.Sessions;
using Voxta.Abstractions.Utils;

namespace Voxta.Modules.Aios.PhilipsHue.Clients;

public class HueUserInteractionWrapper(IChatSessionChatAugmentationApi session) : IHueUserInteractionWrapper
{
    public Task<IUserInteractionRequestToken> RequestUserInteraction(CancellationToken cancellationToken)
    {
        return session.RequestUserAction(new UserInteractionRequestInput
        {
            Message = HuePairingRequest.Message,
            ImageUrl = HuePairingRequest.ImageUrl,
        }, cancellationToken);
    }

}
