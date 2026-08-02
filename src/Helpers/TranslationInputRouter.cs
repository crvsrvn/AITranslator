using System.Text;
using System.Text.RegularExpressions;

namespace AITranslator.Helpers;

public static partial class TranslationInputRouter
{
    public static bool ShouldUseLookup(string value)
    {
        var text = value.Trim();
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length == 0 || runes.Any(Rune.IsWhiteSpace))
        {
            return false;
        }

        if (runes.All(IsHanRune))
        {
            return runes.Length <= 8;
        }

        return SingleTokenPattern().IsMatch(text);
    }

    private static bool IsHanRune(Rune rune) =>
        rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x323AF;

    [GeneratedRegex(@"^(?:[\p{L}\p{N}][\p{L}\p{N}'’+.#_-]*|\.[\p{L}\p{N}][\p{L}\p{N}'’+.#_-]*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SingleTokenPattern();
}
