using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AITranslator.Services;

internal static class DocumentTextLayout
{
    private const uint MarkMissingGlyphs = 1;

    public static string ResolveFontFamily(string? requestedFamily, string text)
    {
        using var systemFont = SystemFonts.MessageBoxFont;
        var candidates = new[]
        {
            NormalizeFontFamily(requestedFamily),
            systemFont?.FontFamily.Name ?? string.Empty,
            "Microsoft YaHei UI",
            "DengXian",
            "SimSun",
            "Segoe UI Symbol",
            "Arial"
        };

        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SupportsText(candidate, text))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("系统中没有可用于文件译文的字体。");
    }

    public static SizeF MeasureText(string text, string family, float size, FontStyle style)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(family, Math.Max(1, size), style, GraphicsUnit.Point);
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        return graphics.MeasureString(text, font, int.MaxValue, format);
    }

    public static FontStyle ToFontStyle(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => FontStyle.Bold | FontStyle.Italic,
        (true, false) => FontStyle.Bold,
        (false, true) => FontStyle.Italic,
        _ => FontStyle.Regular
    };

    public static string NormalizeSingleLine(string value)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', lines).Trim();
    }

    public static string[] DistributeText(string value, IReadOnlyList<int> sourceLengths)
    {
        if (sourceLengths.Count == 0)
        {
            return [];
        }

        if (sourceLengths.Count == 1)
        {
            return [value];
        }

        var totalLength = sourceLengths.Sum();
        if (totalLength <= 0)
        {
            return [value, .. Enumerable.Repeat(string.Empty, sourceLengths.Count - 1)];
        }

        var result = new string[sourceLengths.Count];
        var sourceEnd = 0;
        var targetStart = 0;
        for (var index = 0; index < sourceLengths.Count - 1; index++)
        {
            sourceEnd += sourceLengths[index];
            var proportionalEnd = (int)Math.Round(value.Length * sourceEnd / (double)totalLength);
            var targetEnd = FindNearbyWordBoundary(value, proportionalEnd, targetStart);
            result[index] = value[targetStart..targetEnd];
            targetStart = targetEnd;
        }

        result[^1] = value[targetStart..];
        return result;
    }

    private static int FindNearbyWordBoundary(string value, int proposed, int minimum)
    {
        proposed = Math.Clamp(proposed, minimum, value.Length);
        if (proposed == minimum || proposed == value.Length || char.IsWhiteSpace(value[proposed - 1]) || char.IsWhiteSpace(value[proposed]))
        {
            return proposed;
        }

        const int maximumDistance = 6;
        for (var distance = 1; distance <= maximumDistance; distance++)
        {
            var after = proposed + distance;
            if (after < value.Length && (char.IsWhiteSpace(value[after - 1]) || char.IsWhiteSpace(value[after])))
            {
                return after;
            }

            var before = proposed - distance;
            if (before > minimum && (char.IsWhiteSpace(value[before - 1]) || char.IsWhiteSpace(value[before])))
            {
                return before;
            }
        }

        return proposed;
    }

    private static bool SupportsText(string family, string text)
    {
        try
        {
            using var font = new Font(family, 12, FontStyle.Regular, GraphicsUnit.Point);
            using var bitmap = new Bitmap(1, 1);
            using var graphics = Graphics.FromImage(bitmap);
            var deviceContext = graphics.GetHdc();
            var fontHandle = font.ToHfont();
            var previousFont = SelectObject(deviceContext, fontHandle);
            try
            {
                var glyphs = new ushort[text.Length];
                var result = GetGlyphIndices(deviceContext, text, text.Length, glyphs, MarkMissingGlyphs);
                if (result == uint.MaxValue)
                {
                    return false;
                }

                for (var index = 0; index < text.Length; index++)
                {
                    if (!char.IsWhiteSpace(text[index]) && !char.IsControl(text[index]) && !char.IsSurrogate(text[index]) &&
                        glyphs[index] == ushort.MaxValue)
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                SelectObject(deviceContext, previousFont);
                DeleteObject(fontHandle);
                graphics.ReleaseHdc(deviceContext);
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NormalizeFontFamily(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        var subsetSeparator = name.IndexOf('+');
        if (subsetSeparator >= 0 && subsetSeparator < name.Length - 1)
        {
            name = name[(subsetSeparator + 1)..];
        }

        if (name.StartsWith("TimesNewRoman", StringComparison.OrdinalIgnoreCase))
        {
            return "Times New Roman";
        }

        if (name.StartsWith("Arial", StringComparison.OrdinalIgnoreCase))
        {
            return "Arial";
        }

        var styleSeparator = name.IndexOf('-');
        if (styleSeparator > 0)
        {
            name = name[..styleSeparator];
        }

        return name.EndsWith("PSMT", StringComparison.OrdinalIgnoreCase) ? name[..^4] :
            name.EndsWith("MT", StringComparison.OrdinalIgnoreCase) ? name[..^2] : name;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetGlyphIndices(nint deviceContext, string text, int characterCount, [Out] ushort[] glyphIndices, uint flags);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint deviceContext, nint value);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
}