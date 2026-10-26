using System.Net;

namespace Voxta.Modules.Aios.Spotify.Configuration;

internal static class SpotifyRedirectUri
{
    public const string Default = "http://127.0.0.1:5385/extensions/spotify/oauth2/callback";
    public const string CallbackPath = "/extensions/spotify/oauth2/callback";

    public static string? GetError(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "http" ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address) ||
            uri.Port is 5384 or 80 || uri.AbsolutePath != CallbackPath ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            return $"Set the Spotify Redirect URI in both Voxta and the Spotify Developer Dashboard to {Default}. " +
                   "Spotify authorization uses a temporary local callback listener on its own port. The Voxta web UI port and a period after 'callback' do not work.";
        return null;
    }
}
