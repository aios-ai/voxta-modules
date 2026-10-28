using Voxta.Abstractions.Services.Agents;
using Voxta.Modules.Aios.Spotify.Tools;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;
using Voxta.Model.ApiMessages.Requests;
using Voxta.Model.ApiMessages.Responses;
using Voxta.Abstractions.Repositories;
using Voxta.Modules.Aios.Tools;

namespace Voxta.Modules.Aios.Spotify.Configuration;

public class ModuleConfigurationProvider(IUnitOfWorkFactory unitOfWorkFactory, SpotifyOAuthFlows flows) : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static readonly FormInvokeActionField Reauthenticate = new()
    {
        Name = "ReauthenticateSpotify",
        Label = "Spotify Authorization Recovery",
        ButtonText = "Re-authenticate",
        Text = "If Spotify authorization fails, clear the saved authorization here. Then reopen settings and sign in using Spotify Account. Your Client ID and Client Secret are kept. Restart active chats after signing in.",
    };

    public override async Task<FormInvokeActionResponse> InvokeAction(IAuthenticationContext auth, Guid moduleId,
        StaticSettingsSource settings, FormInvokeActionRequest request, CancellationToken cancellationToken)
    {
        if (request.FieldName != Reauthenticate.Name)
            throw new NotSupportedException("Unknown Spotify settings action.");
        await ModuleAuthorizationReset.ClearAsync(unitOfWorkFactory, auth.UserId, moduleId,
            VoxtaModule.ServiceName, SpotifyAccount.Name, cancellationToken);
        flows.Cancel(auth.UserId, moduleId);
        return new() { Text = "Spotify authorization cleared. Reopen these settings, then click **Sign in** under **Spotify Account** to authorize again. Restart active chats after signing in." };
    }
    public static string[] FieldsRequiringReload => [ClientId.Name, ClientSecret.Name, SpotifyAccount.Name, "Tools", "SpotifyTools", "SpotifyToolsWithArguments", SpotifyToolsSettings.CardsFieldName, ChatToolSettingsContributions.ToolChatStyles.Name, SpeechDuckingMode.Name, SpeechDuckingVolumePercent.Name, SpeechDuckingReductionPercent.Name, SpecialPlaylists.Name];
    public static readonly FormDocumentationField SpotifySetup = FormDocumentationField.Create("""
        Create or open your application in the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard/).
        Add **both** redirect URIs to its settings:

        - `http://127.0.0.1:5384/oauth-native-bounce.html` (desktop app)
        - `http://127.0.0.1:5384/auth/modules/callback` (web browser)

        Enter your Client ID and Client Secret below, save, then sign in to Spotify Account.
        Voxta automatically supplies the correct callback for the client you use. For a different server address,
        register both paths with that address; remote addresses require HTTPS and local HTTP must use `127.0.0.1` or `[::1]`.
        """);
    public static readonly FormAuthorizeField SpotifyAccount = new()
    {
        Name = "SpotifyAccount",
        Label = "Spotify Account",
        Method = AuthorizationMethod.OAuth2,
        Text = "Save your client credentials and register both redirect URLs above, then sign in here.",
    };

    public static readonly FormTextField ClientId = new()
    {
        Name = "ClientId",
        Label = "Client ID",
        Required = true,
        //language=markdown
        Text = "Get your Client ID from your Spotify Developer Dashboard at [developer.spotify.com](https://developer.spotify.com/dashboard/).",
    };

    public static readonly FormPasswordField ClientSecret = new()
    {
        Name = "ClientSecret",
        Label = "Client Secret",
        Required = true,
        //language=markdown
        Text = "Get your Client Secret from your Spotify Developer Dashboard at [developer.spotify.com](https://developer.spotify.com/dashboard/).",
    };

    public static readonly FormChoicesField SpeechDuckingMode = new()
    {
        Name = "SpeechDuckingMode",
        Label = "Speech Ducking Mode",
        Choices =
        [
            new FormChoice { Value = "fixed", Label = "Fixed volume limit" },
            new FormChoice { Value = "relative", Label = "Reduce current volume by a percentage" },
        ],
        DefaultValue = "fixed",
        Text = "Temporarily lower Spotify during speech or transcription, then restore it. Neither mode raises a quieter player.",
    };

    public static readonly FormIntSliderField SpeechDuckingReductionPercent = new()
    {
        Name = "SpeechDuckingReductionPercent",
        Label = "Speech Ducking Reduction (%)",
        Text = "Used in percentage reduction mode. 25 reduces a volume of 60% to 45%. Set to 0 to disable ducking in this mode.",
        Min = 0, Max = 100, SoftMin = 0, SoftMax = 100, Step = 1, DefaultValue = 25,
    };

    public static readonly FormIntSliderField SpeechDuckingVolumePercent = new()
    {
        Name = "SpeechDuckingVolumePercent",
        Label = "Speech Ducking Volume Limit (%)",
        Text = "Used in fixed volume limit mode. Limits Spotify to this volume during speech or transcription; quieter playback is unchanged. Set to 100 to disable ducking in this mode.",
        Min = 0,
        Max = 100,
        SoftMin = 0,
        SoftMax = 100,
        Step = 1,
        DefaultValue = 60
    };

    public static readonly FormMultilineField SpecialPlaylists = new()
    {
        Name = "SpecialPlaylists",
        Label = "Special Spotify Playlists",
        Required = false,
        Text = "You can map your algorithmic Spotify playlists here. Add one entry per line in the format: Name=PlaylistId (e.g. Release Radar=123abc).",
        Rows = 8,
        DefaultValue =
            @"Release Radar=
Discover Weekly=
Daily Mix 1=
Daily Mix 2=
Daily Mix 3=
Daily Mix 4=
Daily Mix 5=
Daily Mix 6="
    };

    public Task<FormField[]> GetModuleConfigurationFieldsAsync(
        IAuthenticationContext auth,
        ISettingsSource settings,
        CancellationToken cancellationToken
    )
    {
        var fields = FormBuilder.Build(
            SpotifySetup,
            ClientId,
            ClientSecret,
            SpotifyAccount,
            Reauthenticate,
            SpeechDuckingMode,
            SpeechDuckingVolumePercent,
            SpeechDuckingReductionPercent,
            SpecialPlaylists,
            SpotifyToolsSettings.ConfigurationField(settings),
            SpotifyToolsSettings.Styles.AsField()
        );
        return Task.FromResult(fields);
    }
}
