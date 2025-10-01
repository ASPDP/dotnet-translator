using System.Text.RegularExpressions;

namespace HotkeyListener.Services.Translation;

internal sealed class LanguageDirectionResolver
{
    private static readonly Regex CyrillicRegex = new("[\\u0400-\\u04FF]", RegexOptions.Compiled);

    public (string From, string To) Resolve(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ("en", "ru");
        }

        return CyrillicRegex.IsMatch(text)
            ? ("ru", "en")
            : ("en", "ru");
    }
}
