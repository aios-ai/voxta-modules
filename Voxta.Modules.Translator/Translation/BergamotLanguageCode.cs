using System.Globalization;
using System.Text.RegularExpressions;

namespace Voxta.Modules.Translator.Translation;

internal static partial class BergamotLanguageCode
{
    public static string Normalize(string code) => code.Trim().ToLowerInvariant().Replace('-', '_');
    public static bool IsValid(string code) => Pattern().IsMatch(code);

    // Mozilla uses underscores within language codes and a hyphen between source and target.
    [GeneratedRegex(@"\A[a-z]{2,3}(?:_[a-z0-9]{2,8})*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string FromCulture(CultureInfo? culture)
    {
        for (var current = culture; current is not null && current.Name.Length > 0; current = current.Parent)
        {
            if (string.Equals(current.Name, "zh-Hant", StringComparison.OrdinalIgnoreCase))
                return "zh_hant";
        }

        return culture?.TwoLetterISOLanguageName ?? "en";
    }
}
