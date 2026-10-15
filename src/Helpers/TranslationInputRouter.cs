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

    public const string ChatMarker = "@@";

    // 输入以 @@（兼容全角 ＠）开头时，其后内容作为 AI 对话发送，不再翻译；message 为空表示清空对话。
    public static bool TryParseChat(string value, out string message)
    {
        var text = value.TrimStart();
        var isChat = text.Length >= 2 && IsChatMarkerChar(text[0]) && IsChatMarkerChar(text[1]);
        message = isChat ? text[2..].Trim() : string.Empty;
        return isChat;
    }

    private static bool IsChatMarkerChar(char value) => value is '@' or '＠';

    private static bool IsHanRune(Rune rune) =>
        rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x323AF;

    [GeneratedRegex(@"^(?:[\p{L}\p{N}][\p{L}\p{N}'’+.#_-]*|\.[\p{L}\p{N}][\p{L}\p{N}'’+.#_-]*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SingleTokenPattern();
}
