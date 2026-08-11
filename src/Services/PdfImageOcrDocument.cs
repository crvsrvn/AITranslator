using System.Drawing;
using AITranslator.Models;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using WindowsPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace AITranslator.Services;

internal sealed class PdfImageOcrDocument
{
    private const double PreferredRenderScale = 2;
    private readonly WindowsPdfDocument _document;
    private readonly OcrService _ocr;

    private PdfImageOcrDocument(WindowsPdfDocument document, OcrService ocr)
    {
        _document = document;
        _ocr = ocr;
    }

    public uint PageCount => _document.PageCount;

    public static async Task<PdfImageOcrDocument> OpenAsync(string sourcePath, OcrService ocr, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(sourcePath);
        var document = await WindowsPdfDocument.LoadFromFileAsync(file);
        cancellationToken.ThrowIfCancellationRequested();
        return new PdfImageOcrDocument(document, ocr);
    }

    public async Task<IReadOnlyList<PdfImageTextLine>> RecognizePageAsync(uint pageIndex, PdfImagePageGeometry geometry,
        IReadOnlyList<PdfImageBounds> visibleTextRegions, string sourceLanguage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var page = _document.GetPage(pageIndex);
        var pageSize = page.Size;
        if (pageSize.Width <= 0 || pageSize.Height <= 0 || geometry.CropWidth <= 0 || geometry.CropHeight <= 0)
        {
            return [];
        }

        var maximumDimension = Math.Max(pageSize.Width, pageSize.Height);
        var scale = Math.Min(PreferredRenderScale, OcrEngine.MaxImageDimension / maximumDimension);
        var renderWidth = (uint)Math.Max(1, Math.Round(pageSize.Width * scale));
        var renderHeight = (uint)Math.Max(1, Math.Round(pageSize.Height * scale));
        var pngBytes = await RenderPageAsync(page, renderWidth, renderHeight, cancellationToken);
        var recognized = await RecognizeBestOrientationAsync(pngBytes, ResolveOcrLanguage(sourceLanguage), cancellationToken);
        if (recognized.Result.Lines.Count == 0)
        {
            return [];
        }

        using var imageStream = new MemoryStream(pngBytes, false);
        using var bitmap = new Bitmap(imageStream);
        var pageRotation = NormalizePageRotation(geometry.Rotation);
        var swapsAxes = pageRotation is 90 or 270;
        var displayWidth = swapsAxes ? geometry.CropHeight : geometry.CropWidth;
        var displayHeight = swapsAxes ? geometry.CropWidth : geometry.CropHeight;
        var xScale = displayWidth / bitmap.Width;
        var yScale = displayHeight / bitmap.Height;
        var result = new List<PdfImageTextLine>(recognized.Result.Lines.Count);
        foreach (var line in recognized.Result.Lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = line.Text.Trim();
            if (!ShouldTranslateImageText(text))
            {
                continue;
            }

            var mappedBounds = MapToOriginalBounds(line.Bounds, recognized.Rotation, bitmap.Width, bitmap.Height);
            var pixelBounds = ClampBounds(mappedBounds.Left, mappedBounds.Top, mappedBounds.Width, mappedBounds.Height, bitmap.Width,
                bitmap.Height);
            if (pixelBounds.Width < 2 || pixelBounds.Height < 2)
            {
                continue;
            }

            var displayBounds = new PdfImageBounds(pixelBounds.Left * xScale, pixelBounds.Top * yScale, pixelBounds.Width * xScale,
                pixelBounds.Height * yScale);
            var bounds = MapDisplayToPageBounds(displayBounds, geometry);
            if (IsCoveredByVisibleText(bounds, visibleTextRegions))
            {
                continue;
            }

            var background = SampleBackground(bitmap, pixelBounds);
            var angle = NormalizeAngle((recognized.Result.TextAngle ?? 0) - recognized.Rotation - pageRotation);
            result.Add(new PdfImageTextLine(text, bounds, angle, background, GetContrastingColor(background)));
        }

        return result;
    }

    private async Task<OrientedOcrResult> RecognizeBestOrientationAsync(byte[] pngBytes, string language,
        CancellationToken cancellationToken)
    {
        var original = await _ocr.RecognizeLayoutAsync(pngBytes, language, cancellationToken);
        var best = new OrientedOcrResult(original, 0);
        var bestScore = Score(original);
        if (original.Lines.Count >= 4 && bestScore >= 40)
        {
            return best;
        }

        foreach (var rotation in new[] { 90, 180, 270 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rotatedBytes = RotatePng(pngBytes, rotation);
            var candidate = await _ocr.RecognizeLayoutAsync(rotatedBytes, language, cancellationToken);
            var candidateScore = Score(candidate);
            if (candidateScore > bestScore)
            {
                best = new OrientedOcrResult(candidate, rotation);
                bestScore = candidateScore;
            }
        }

        return best;
    }

    private static int Score(OcrCaptureResult result) =>
        result.Lines.Sum(line => line.Text.Count(char.IsLetter));

    private static byte[] RotatePng(byte[] pngBytes, int rotation)
    {
        using var sourceStream = new MemoryStream(pngBytes, false);
        using var source = new Bitmap(sourceStream);
        using var rotated = new Bitmap(source);
        rotated.RotateFlip(rotation switch
        {
            90 => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _ => throw new ArgumentOutOfRangeException(nameof(rotation))
        });
        using var output = new MemoryStream();
        rotated.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        return output.ToArray();
    }

    private static PdfImageBounds MapToOriginalBounds(global::Windows.Foundation.Rect bounds, int rotation, int imageWidth,
        int imageHeight) => rotation switch
    {
        90 => new PdfImageBounds(bounds.Y, imageHeight - bounds.X - bounds.Width, bounds.Height, bounds.Width),
        180 => new PdfImageBounds(imageWidth - bounds.X - bounds.Width, imageHeight - bounds.Y - bounds.Height, bounds.Width,
            bounds.Height),
        270 => new PdfImageBounds(imageWidth - bounds.Y - bounds.Height, bounds.X, bounds.Height, bounds.Width),
        _ => new PdfImageBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height)
    };

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180)
        {
            angle -= 360;
        }

        while (angle <= -180)
        {
            angle += 360;
        }

        return angle;
    }

    private static int NormalizePageRotation(int rotation)
    {
        var normalized = ((rotation % 360) + 360) % 360;
        return normalized is 0 or 90 or 180 or 270
            ? normalized
            : throw new InvalidDataException($"PDF 页面旋转角度 {rotation} 无效，无法定位图像文字。");
    }

    internal static PdfImageBounds MapDisplayToPageBounds(PdfImageBounds bounds, PdfImagePageGeometry geometry)
    {
        var rotation = NormalizePageRotation(geometry.Rotation);
        var topOffset = geometry.PageHeight - geometry.CropBottom - geometry.CropHeight;
        return rotation switch
        {
            90 => new PdfImageBounds(geometry.CropLeft + bounds.Top,
                topOffset + geometry.CropHeight - bounds.Right, bounds.Height, bounds.Width),
            180 => new PdfImageBounds(geometry.CropLeft + geometry.CropWidth - bounds.Right,
                topOffset + geometry.CropHeight - bounds.Bottom, bounds.Width, bounds.Height),
            270 => new PdfImageBounds(geometry.CropLeft + geometry.CropWidth - bounds.Bottom,
                topOffset + bounds.Left, bounds.Height, bounds.Width),
            _ => new PdfImageBounds(geometry.CropLeft + bounds.Left, topOffset + bounds.Top, bounds.Width, bounds.Height)
        };
    }

    private static bool ShouldTranslateImageText(string text)
    {
        var letters = text.Where(char.IsLetter).ToArray();
        return letters.Length > 0 && !(letters.Length == 1 && letters[0] <= '\u007F');
    }

    private static async Task<byte[]> RenderPageAsync(PdfPage page, uint width, uint height, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height,
            BitmapEncoderId = BitmapEncoder.PngEncoderId,
            IsIgnoringHighContrast = true
        };
        await page.RenderToStreamAsync(stream, options);
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Size > int.MaxValue)
        {
            throw new InvalidDataException("PDF 页面渲染结果过大，无法执行图像文字识别。");
        }

        stream.Seek(0);
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var length = (uint)stream.Size;
        await reader.LoadAsync(length);
        var bytes = new byte[(int)length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static Rectangle ClampBounds(double left, double top, double width, double height, int imageWidth, int imageHeight)
    {
        var x = Math.Clamp((int)Math.Floor(left), 0, imageWidth - 1);
        var y = Math.Clamp((int)Math.Floor(top), 0, imageHeight - 1);
        var right = Math.Clamp((int)Math.Ceiling(left + width), x + 1, imageWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(top + height), y + 1, imageHeight);
        return Rectangle.FromLTRB(x, y, right, bottom);
    }

    private static bool IsCoveredByVisibleText(PdfImageBounds candidate, IReadOnlyList<PdfImageBounds> visibleTextRegions)
    {
        foreach (var region in visibleTextRegions)
        {
            var intersectionWidth = Math.Max(0, Math.Min(candidate.Right, region.Right) - Math.Max(candidate.Left, region.Left));
            var intersectionHeight = Math.Max(0, Math.Min(candidate.Bottom, region.Bottom) - Math.Max(candidate.Top, region.Top));
            var intersectionArea = intersectionWidth * intersectionHeight;
            if (intersectionArea / Math.Max(1, Math.Min(candidate.Area, region.Area)) >= 0.35)
            {
                return true;
            }
        }

        return false;
    }

    private static PdfImageColor SampleBackground(Bitmap bitmap, Rectangle bounds)
    {
        var offset = Math.Max(2, bounds.Height / 5);
        var left = Math.Max(0, bounds.Left - offset);
        var right = Math.Min(bitmap.Width - 1, bounds.Right + offset - 1);
        var top = Math.Max(0, bounds.Top - offset);
        var bottom = Math.Min(bitmap.Height - 1, bounds.Bottom + offset - 1);
        var horizontalStep = Math.Max(1, bounds.Width / 24);
        var verticalStep = Math.Max(1, bounds.Height / 12);
        var samples = new List<Color>();
        for (var x = bounds.Left; x < bounds.Right; x += horizontalStep)
        {
            samples.Add(bitmap.GetPixel(x, top));
            samples.Add(bitmap.GetPixel(x, bottom));
        }

        for (var y = bounds.Top; y < bounds.Bottom; y += verticalStep)
        {
            samples.Add(bitmap.GetPixel(left, y));
            samples.Add(bitmap.GetPixel(right, y));
        }

        if (samples.Count == 0)
        {
            return new PdfImageColor(255, 255, 255);
        }

        var dominant = samples.GroupBy(color => (color.R / 32, color.G / 32, color.B / 32))
            .OrderByDescending(group => group.Count()).First().ToArray();
        return new PdfImageColor((byte)Math.Round(dominant.Average(color => color.R)),
            (byte)Math.Round(dominant.Average(color => color.G)), (byte)Math.Round(dominant.Average(color => color.B)));
    }

    private static PdfImageColor GetContrastingColor(PdfImageColor background)
    {
        var luminance = 0.299 * background.Red + 0.587 * background.Green + 0.114 * background.Blue;
        return luminance >= 150 ? new PdfImageColor(18, 18, 18) : new PdfImageColor(245, 245, 245);
    }

    private static string ResolveOcrLanguage(string sourceLanguage)
    {
        if (sourceLanguage.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            sourceLanguage.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-Hant";
        }

        if (sourceLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-Hans";
        }

        return sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
               sourceLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : sourceLanguage;
    }
}

internal readonly record struct PdfImageBounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public double Area => Math.Max(0, Width) * Math.Max(0, Height);
}

internal readonly record struct PdfImageColor(byte Red, byte Green, byte Blue);

internal readonly record struct PdfImagePageGeometry(
    double PageWidth,
    double PageHeight,
    double CropLeft,
    double CropBottom,
    double CropWidth,
    double CropHeight,
    int Rotation);

internal sealed record PdfImageTextLine(
    string Text,
    PdfImageBounds Bounds,
    double Angle,
    PdfImageColor Background,
    PdfImageColor Foreground)
{
    public int? TranslationId { get; set; }
}

internal sealed record OrientedOcrResult(OcrCaptureResult Result, int Rotation);
