using Voxta.Abstractions.Services.Agents;
using Voxta.Modules.Aios.Spotify.Tools;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Model.Shared.Forms;

namespace Voxta.Modules.Aios.Spotify.Configuration;

public class ModuleConfigurationProvider : ModuleConfigurationProviderBase, IModuleConfigurationProvider
{
    public static string[] FieldsRequiringReload => [ClientId.Name, ClientSecret.Name, SpotifyAccount.Name, "Tools", "SpotifyTools", ChatToolSettingsContributions.ToolChatStyles.Name, SpeechDuckingVolumePercent.Name, SpecialPlaylists.Name];
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

    public static readonly FormIntSliderField SpeechDuckingVolumePercent = new()
    {
        Name = "SpeechDuckingVolumePercent",
        Label = "Speech Ducking Volume",
        Text = "Spotify volume while speech or transcription is active. Set to 100 to disable volume ducking.",
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
            SpeechDuckingVolumePercent,
            SpecialPlaylists,
            SpotifyToolsSettings.ConfigurationField(settings),
            SpotifyToolsSettings.Styles.AsField()
        );
        return Task.FromResult(fields);
    }
}
