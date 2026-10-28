using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.Aios.PhilipsHue.Tools;
using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Authorization;
using Voxta.Abstractions.Connections;
using Voxta.Abstractions.Encryption;
using Voxta.Abstractions.Repositories;
using Voxta.Abstractions.Utils;
using Voxta.Model.ApiMessages.Requests;
using Voxta.Model.ApiMessages.Responses;
using Voxta.Modules.Aios.PhilipsHue.Clients;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.PhilipsHue.Configuration;

public class ModuleConfigurationProvider(IUnitOfWorkFactory unitOfWorkFactory,
    IModuleOAuthTokenStore tokenStore, ILocalEncryptionProvider encryption,
    IUserInteractionRequestsManager interactions, IIVoxtaWebsocketBroadcasterFactory broadcasts,
    ILoggerFactory loggerFactory) : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static readonly FormInvokeActionField Reauthenticate = new()
    {
        Name = "ReauthenticateHue",
        Label = "Hue Bridge Authorization Recovery",
        ButtonText = "Re-authenticate",
        Text = "Clear the saved bridge key and pair again if Hue authorization fails. Press the link button on your Hue bridge when prompted. Restart active chats after pairing.",
    };

    public override async Task<FormInvokeActionResponse> InvokeAction(IAuthenticationContext auth, Guid moduleId,
        StaticSettingsSource settings, FormInvokeActionRequest request, CancellationToken cancellationToken)
    {
        if (request.FieldName != Reauthenticate.Name)
            throw new NotSupportedException("Unknown Hue settings action.");
        await ModuleAuthorizationReset.ClearAsync(unitOfWorkFactory, auth.UserId, moduleId,
            VoxtaModule.ServiceName, HueVoxtaCredentialStore.Field, cancellationToken);
        var bridge = new HueBridgeConnectionService(loggerFactory.CreateLogger<HueBridgeConnectionService>(),
            new TestHueUserInteractionWrapper(interactions, broadcasts.Create(auth.UserId), moduleId),
            new HueVoxtaCredentialStore(tokenStore, encryption, auth.UserId, moduleId));
        var connected = await bridge.InitializeBridgeAsync(cancellationToken);
        return new() { Text = connected
            ? "Hue bridge paired again. The new credentials are saved in Voxta's encrypted store. Restart active chats to use them."
            : (bridge.LastUserVisibleError ?? "Hue pairing failed.") + " Click **Re-authenticate** to try again." };
    }
    public static string[] FieldsRequiringReload => ["HueToolCards", "ToolChatStyles", CharacterControlledLight.Name];
    public static readonly FormTextField CharacterControlledLight = new()
    {
        Name = "CharacterControlledLight",
        Label = "Character Controlled Light",
        DefaultValue = "",
        Text = "Target light, zone or room name the character can control on it's own (optional)",
    };

   public Task<FormField[]> GetModuleConfigurationFieldsAsync(
        IAuthenticationContext auth,
        ISettingsSource settings,
        CancellationToken cancellationToken
    )
   {
       var fields = FormBuilder.Build(
           Reauthenticate,
           CharacterControlledLight,
           HueToolsSettings.ConfigurationField(settings),
           HueToolsSettings.Styles.AsField()
       );
        return Task.FromResult(fields);
    }
}
