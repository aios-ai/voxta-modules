using System.Net;

namespace Voxta.Modules.Aios.Spotify.Configuration;

internal static class SpotifyRedirectUri
{
    public const string VoxtaCallbackPath = "/auth/modules/callback";
    public const string VoxtaDefault = "http://127.0.0.1:5384/auth/modules/callback";
    public const string DesktopCallbackPath = "/oauth-native-bounce.html";
    public const string DesktopDefault = "http://127.0.0.1:5384/oauth-native-bounce.html";

    public static string? GetError(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)) ||
            uri.AbsolutePath is not (VoxtaCallbackPath or DesktopCallbackPath) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            return $"Use {DesktopDefault} for the desktop app or {VoxtaDefault} for a web browser, with your Voxta server's origin. HTTP requires an explicit loopback IP; remote addresses require HTTPS.";
        return null;
    }

}
