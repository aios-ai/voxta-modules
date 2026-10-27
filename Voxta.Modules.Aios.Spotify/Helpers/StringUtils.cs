using System.Text.RegularExpressions;

namespace Voxta.Modules.Aios.Spotify.Helpers;

public static class StringUtils
{
    public static string CleanString(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        input = Regex.Replace(input, @"[^\w\s]", "");
        input = Regex.Replace(input, @"\s+", " ").Trim();

        return input;
    }
    
    public static string NormaliseKey(string input)
    {
        return CleanString(input).ToLowerInvariant();
    }

    public static string FormatMillisecondsToMinutesSeconds(int milliseconds)
    {
        var t = TimeSpan.FromMilliseconds(milliseconds);
        return string.Format("{0:D2}:{1:D2}", (int)t.TotalMinutes, t.Seconds);
    }

    public static string ExtractIdFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return string.Empty;

        var parts = uri.Split(':');
        return parts.Length > 0 ? parts[^1] : string.Empty;
    }
    
    public static string NormaliseSpecialName(string input)
    {
        input = input.Trim().ToLowerInvariant();

        if (input.Contains("radar"))
            return "release radar";

        if (input.Contains("discover") || input.Contains("weekly"))
            return "discover weekly";

        if (input.Contains("daily") || input.Contains("mix") || input.Contains("mixtape"))
        {
            var match = Regex.Match(input, @"\d+");
            if (match.Success)
                return $"daily mix {match.Value}";
            return "daily mix";
        }

        return input;
    }
}