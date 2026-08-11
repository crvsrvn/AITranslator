using AITranslator.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace AITranslator.Services;

public sealed class OcrService
{
    public async Task<string> RecognizeAsync(byte[] pngBytes, string preferredLanguage = "zh-Hans", CancellationToken cancellationToken = default)
    {
        var result = await RecognizeLayoutAsync(pngBytes, preferredLanguage, cancellationToken);
        return result.Text;
    }

    public async Task<OcrCaptureResult> RecognizeLayoutAsync(byte[] pngBytes, string preferredLanguage = "zh-Hans",
        CancellationToken cancellationToken = default)
    {
        if (pngBytes.Length == 0)
        {
            return new OcrCaptureResult(string.Empty, []);
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var engines = CreateEngines(preferredLanguage);
        if (engines.Count == 0)
        {
            throw new InvalidOperationException("系统未安装可用的 OCR 语言包。");
        }

        OcrCaptureResult best = new(string.Empty, []);
        var bestScore = 0;
        foreach (var engine in engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recognized = await engine.RecognizeAsync(bitmap);
            var candidate = CreateCaptureResult(recognized);
            var score = candidate.Text.Count(character => !char.IsWhiteSpace(character));
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private static IReadOnlyList<OcrEngine> CreateEngines(string preferredLanguage)
    {
        var engines = new List<OcrEngine>();
        var languageTags = new[] { preferredLanguage, "zh-Hans", "en-US" }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var languageTag in languageTags)
        {
            try
            {
                var language = new Language(languageTag);
                if (OcrEngine.IsLanguageSupported(language) && OcrEngine.TryCreateFromLanguage(language) is { } engine)
                {
                    engines.Add(engine);
                }
            }
            catch (ArgumentException)
            {
                // 无效或未安装的语言由后续候选项接替。
            }
        }

        if (OcrEngine.TryCreateFromUserProfileLanguages() is { } profileEngine)
        {
            engines.Add(profileEngine);
        }

        return engines;
    }

    private static OcrCaptureResult CreateCaptureResult(OcrResult result)
    {
        var lines = new List<OcrTextLine>();
        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (text.Length == 0 || line.Words.Count == 0)
            {
                continue;
            }

            var left = line.Words.Min(word => word.BoundingRect.X);
            var top = line.Words.Min(word => word.BoundingRect.Y);
            var right = line.Words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
            var bottom = line.Words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
            lines.Add(new OcrTextLine(text, new global::Windows.Foundation.Rect(left, top, right - left, bottom - top)));
        }

        return new OcrCaptureResult(string.Join(Environment.NewLine, lines.Select(line => line.Text)), lines, result.TextAngle);
    }
}
