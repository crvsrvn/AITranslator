using AITranslator.Models;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace AITranslator.Services;

internal sealed class PdfLayoutTranslationService
{
    private readonly TranslationOrchestrator _translator;

    public PdfLayoutTranslationService(TranslationOrchestrator translator)
    {
        _translator = translator;
    }

    public async Task<int> TranslateAsync(string sourcePath, string outputPath, string sourceLanguage, string targetLanguage, string domain,
        IProgress<FileTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        using var sourceDocument = PdfPigDocument.Open(sourcePath);
        using var outputDocument = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);
        if (sourceDocument.NumberOfPages != outputDocument.PageCount)
        {
            throw new InvalidDataException("PDF 页面结构不一致，无法安全替换文本。");
        }

        var pagePlans = new List<PdfPagePlan>(sourceDocument.NumberOfPages);
        var requestUnits = new List<DocumentTextUnit>();
        for (var pageIndex = 0; pageIndex < sourceDocument.NumberOfPages; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePage = sourceDocument.GetPage(pageIndex + 1);
            var lines = ExtractLines(sourcePage);
            var blocks = CreateBlocks(lines);
            foreach (var line in lines.Where(line => ShouldTranslate(line.Text)))
            {
                line.TranslationId = requestUnits.Count;
                requestUnits.Add(new DocumentTextUnit(line.TranslationId.Value, line.Text));
            }

            pagePlans.Add(new PdfPagePlan(pageIndex, sourcePage.Height, lines, blocks));
        }

        if (requestUnits.Count == 0)
        {
            throw new NotSupportedException("PDF 中没有可直接替换的文字对象；扫描件、转曲文字和纯图像页面暂不支持。");
        }

        progress?.Report(new FileTranslationProgress(1, 3, "正在调用翻译 API"));
        await Task.Yield();
        var globalReferenceText = string.Join("\n\n", pagePlans.Select(plan => string.Join('\n', plan.Lines.Select(line => line.Text))));
        var translated = await _translator.TranslateDocumentAsync(
            new DocumentTranslationRequest(requestUnits, sourceLanguage, targetLanguage, domain, globalReferenceText), cancellationToken);
        var translations = new Dictionary<int, string>();
        foreach (var translatedUnit in translated.Units)
        {
            if (!translations.TryAdd(translatedUnit.Id, translatedUnit.Text))
            {
                throw new InvalidDataException("翻译 API 返回了重复的 PDF 文本行标识。");
            }
        }

        if (translations.Count != requestUnits.Count || requestUnits.Any(unit => !translations.ContainsKey(unit.Id)))
        {
            throw new InvalidDataException("翻译 API 返回的 PDF 文本行数量或标识不一致。");
        }

        progress?.Report(new FileTranslationProgress(2, 3, "正在原位替换 PDF 文字对象"));
        await Task.Yield();
        foreach (var pagePlan in pagePlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pagePlan.Blocks.Count == 0)
            {
                continue;
            }

            var outputPage = outputDocument.Pages[pagePlan.PageIndex];
            var replacements = new List<PdfBlockReplacement>(pagePlan.Blocks.Count);
            foreach (var block in pagePlan.Blocks)
            {
                var translatedLines = block.Lines.Select(line => line.TranslationId is int translationId
                    ? DocumentTextLayout.NormalizeSingleLine(translations[translationId])
                    : line.Text).ToArray();

                var sequence = CreateReplacementSequence(outputDocument, outputPage, pagePlan.PageHeight, block, translatedLines);
                replacements.Add(new PdfBlockReplacement(block.TextSequences, sequence));
            }

            ReplacePageTextObjects(outputPage, pagePlan.Lines, replacements, pagePlan.PageIndex + 1);
        }

        outputDocument.Save(outputPath);
        return requestUnits.Count;
    }

    private static CSequence CreateReplacementSequence(PdfDocument document, PdfPage targetPage, double pageHeight, PdfTextBlock block,
        IReadOnlyList<string> translatedLines)
    {
        var temporaryPage = document.AddPage();
        temporaryPage.Width = targetPage.Width;
        temporaryPage.Height = targetPage.Height;
        temporaryPage.Rotate = targetPage.Rotate;
        try
        {
            using (var graphics = XGraphics.FromPdfPage(temporaryPage))
            {
                DrawReplacementBlock(graphics, pageHeight, block, translatedLines);
            }

            var sequence = ContentReader.ReadContent(temporaryPage).Clone();
            MergeFontResources(temporaryPage, targetPage, sequence);
            return sequence;
        }
        finally
        {
            document.Pages.Remove(temporaryPage);
        }
    }

    private static void MergeFontResources(PdfPage sourcePage, PdfPage targetPage, CSequence sequence)
    {
        var sourceFonts = sourcePage.Resources.Elements.GetDictionary("/Font");
        if (sourceFonts is null)
        {
            throw new InvalidDataException("PDF 译文字体资源未生成。");
        }

        var remappedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in sequence.OfType<COperator>().Where(operation => operation.OpCode.OpCodeName == OpCodeName.Tf))
        {
            if (operation.Operands.Count == 0 || operation.Operands[0] is not CName fontName)
            {
                throw new InvalidDataException("PDF 译文字体操作无效。");
            }

            if (!remappedNames.TryGetValue(fontName.Name, out var targetName))
            {
                var font = sourceFonts.Elements.GetObject(fontName.Name) as PdfFont ?? throw new InvalidDataException("PDF 译文字体资源无法读取。");
                targetName = targetPage.Resources.AddFont(font);
                remappedNames[fontName.Name] = targetName;
            }

            fontName.Name = targetName;
        }
    }

    private static void ReplacePageTextObjects(PdfPage page, IReadOnlyList<PdfTextLine> lines, IReadOnlyList<PdfBlockReplacement> replacements,
        int pageNumber)
    {
        var content = ContentReader.ReadContent(page);
        var ranges = FindTextObjectRanges(content);
        var visibleSequences = lines.SelectMany(line => line.TextSequences).ToHashSet();
        var replacementByFirstSequence = new Dictionary<int, CSequence>();
        var replacementSequences = new HashSet<int>();
        foreach (var replacement in replacements)
        {
            if (replacement.TextSequences.Count == 0 || replacement.TextSequences.Any(sequence => !replacementSequences.Add(sequence)))
            {
                throw new NotSupportedException($"PDF 第 {pageNumber} 页的文字绘制操作相互交叠，无法安全原位替换。");
            }

            replacementByFirstSequence[replacement.TextSequences.Min()] = replacement.Sequence;
        }

        var mappedSequences = ranges.SelectMany(range => range.TextSequences).ToHashSet();
        if (!visibleSequences.IsSubsetOf(mappedSequences) || !replacementSequences.IsSubsetOf(mappedSequences))
        {
            throw new NotSupportedException($"PDF 第 {pageNumber} 页的文字位于嵌套表单或不受支持的内容流中，无法安全原位替换。");
        }

        var rangeByStart = ranges.Where(range => range.TextSequences.Any(visibleSequences.Contains)).ToDictionary(range => range.StartIndex);
        var rewritten = new CSequence();
        for (var index = 0; index < content.Count; index++)
        {
            if (!rangeByStart.TryGetValue(index, out var range))
            {
                rewritten.Add(content[index]);
                continue;
            }

            foreach (var sequence in range.TextSequences.Order())
            {
                if (replacementByFirstSequence.TryGetValue(sequence, out var replacement))
                {
                    rewritten.Add(replacement);
                }
            }

            index = range.EndIndex;
        }

        page.Contents.ReplaceContent(rewritten);
    }

    private static IReadOnlyList<TextObjectRange> FindTextObjectRanges(CSequence content)
    {
        var result = new List<TextObjectRange>();
        var textSequence = 1;
        var startIndex = -1;
        List<int>? sequences = null;
        for (var index = 0; index < content.Count; index++)
        {
            if (content[index] is not COperator operation)
            {
                continue;
            }

            if (operation.OpCode.OpCodeName == OpCodeName.BT)
            {
                if (startIndex >= 0)
                {
                    throw new NotSupportedException("PDF 内容流包含嵌套文字对象，无法安全替换。");
                }

                startIndex = index;
                sequences = [];
                continue;
            }

            if (IsTextDrawingOperation(operation))
            {
                sequences?.Add(textSequence);
                textSequence++;
                continue;
            }

            if (operation.OpCode.OpCodeName == OpCodeName.ET && startIndex >= 0 && sequences is not null)
            {
                result.Add(new TextObjectRange(startIndex, index, sequences));
                startIndex = -1;
                sequences = null;
            }
        }

        if (startIndex >= 0)
        {
            throw new NotSupportedException("PDF 内容流的文字对象没有正常结束，无法安全替换。");
        }

        return result;
    }

    private static void DrawReplacementBlock(XGraphics graphics, double pageHeight, PdfTextBlock block, IReadOnlyList<string> translatedLines)
    {
        var preparedLines = block.Lines.Select((line, index) => PrepareLine(graphics, line, translatedLines[index])).ToArray();
        if (preparedLines.Length == 1)
        {
            var prepared = preparedLines[0];
            var scale = prepared.Width <= 0 ? 1 : prepared.Line.Width / prepared.Width;
            DrawPreparedLine(graphics, pageHeight, prepared, Math.Clamp(scale, 0.05, 20), null);
            return;
        }

        var blockHeight = Math.Max(1, block.Top - block.Bottom);
        var naturalHeight = Math.Max(1, preparedLines.Sum(line => line.Height));
        var scaleFactor = Math.Clamp(blockHeight / naturalHeight, 0.05, 20);
        var scaledHeights = preparedLines.Select(line => line.Height * scaleFactor).ToArray();
        var currentTop = block.Top - Math.Max(0, blockHeight - scaledHeights.Sum()) / 2;
        for (var index = 0; index < preparedLines.Length; index++)
        {
            var lineHeight = scaledHeights[index];
            DrawPreparedLine(graphics, pageHeight, preparedLines[index], scaleFactor, (currentTop - lineHeight, currentTop));
            currentTop -= lineHeight;
        }
    }

    private static PreparedPdfLine PrepareLine(XGraphics graphics, PdfTextLine line, string translation)
    {
        var pieces = DocumentTextLayout.DistributeText(translation, line.Spans.Select(span => span.Text.Length).ToArray());
        var spans = new List<PreparedPdfSpan>(line.Spans.Count);
        for (var index = 0; index < line.Spans.Count; index++)
        {
            if (pieces[index].Length == 0)
            {
                continue;
            }

            var sourceSpan = line.Spans[index];
            var requestedFamily = DocumentTextLayout.ResolveFontFamily(sourceSpan.FontName, pieces[index]);
            var style = ResolveFontStyle(sourceSpan.FontName);
            var (family, font) = CreatePdfFont(requestedFamily, sourceSpan.FontSize, style);
            var size = graphics.MeasureString(pieces[index], font);
            spans.Add(new PreparedPdfSpan(pieces[index], family, style, sourceSpan.FontSize, sourceSpan.Color, size.Width, size.Height));
        }

        return new PreparedPdfLine(line, spans, spans.Sum(span => span.Width), spans.Count == 0 ? line.Height : spans.Max(span => span.Height));
    }

    private static (string Family, XFont Font) CreatePdfFont(string requestedFamily, double size, XFontStyleEx style)
    {
        foreach (var family in new[] { requestedFamily, "DengXian" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return (family, new XFont(family, Math.Clamp(size, 1, 400), style));
            }
            catch (InvalidOperationException)
            {
                // PDFsharp 可能无法嵌入 GDI 能测量的系统 UI 字体，继续使用可嵌入的中文系统字体。
            }
        }

        throw new InvalidOperationException($"PDFsharp 无法嵌入字体“{requestedFamily}”或中文回退字体“DengXian”。");
    }

    private static void DrawPreparedLine(XGraphics graphics, double pageHeight, PreparedPdfLine prepared, double scale,
        (double Bottom, double Top)? bounds)
    {
        if (prepared.Spans.Count == 0)
        {
            return;
        }

        var line = prepared.Line;
        var bottom = bounds?.Bottom ?? line.Bottom;
        var top = bounds?.Top ?? line.Top;
        var width = Math.Max(1, line.Width);
        var height = Math.Max(1, top - bottom);
        var centerX = line.Left + width / 2;
        var centerY = pageHeight - top + height / 2;
        var rotation = line.Orientation switch
        {
            TextOrientation.Rotate90 => 90,
            TextOrientation.Rotate180 => 180,
            TextOrientation.Rotate270 => -90,
            _ => 0
        };

        var drawWidth = rotation is 90 or -90 ? height : width;
        var drawHeight = rotation is 90 or -90 ? width : height;
        var rectangle = new XRect(centerX - drawWidth / 2, centerY - drawHeight / 2, drawWidth, drawHeight);
        var state = graphics.Save();
        if (rotation != 0)
        {
            graphics.RotateAtTransform(rotation, new XPoint(centerX, centerY));
        }

        var x = rectangle.Left;
        foreach (var span in prepared.Spans)
        {
            var font = new XFont(span.FontFamily, Math.Clamp(span.FontSize * scale, 1, 400), span.Style);
            var measured = graphics.MeasureString(span.Text, font);
            var (red, green, blue) = span.Color;
            var color = XColor.FromArgb(ToByte(red), ToByte(green), ToByte(blue));
            graphics.DrawString(span.Text, font, new XSolidBrush(color),
                new XRect(x, rectangle.Top, Math.Max(1, measured.Width + 1), rectangle.Height), XStringFormats.CenterLeft);
            x += measured.Width;
        }

        graphics.Restore(state);
    }

    private static IReadOnlyList<PdfTextBlock> CreateBlocks(IReadOnlyList<PdfTextLine> lines)
    {
        var builders = new List<PdfTextBlockBuilder>();
        foreach (var line in lines)
        {
            var builder = builders.LastOrDefault();
            if (builder is null || !builder.CanAppend(line))
            {
                builders.Add(new PdfTextBlockBuilder(line));
            }
            else
            {
                builder.Append(line);
            }
        }

        return builders.Select(builder => builder.Build()).ToArray();
    }

    private static IReadOnlyList<PdfTextLine> ExtractLines(Page page)
    {
        var words = page.GetWords().Where(word => !string.IsNullOrWhiteSpace(word.Text)).ToArray();
        var horizontalRows = new List<WordRow>();
        var result = new List<PdfTextLine>();
        foreach (var word in words)
        {
            if (word.TextOrientation != TextOrientation.Horizontal)
            {
                result.Add(CreateLine([word]));
                continue;
            }

            var row = horizontalRows.FirstOrDefault(candidate => candidate.CanAccept(word));
            if (row is null)
            {
                horizontalRows.Add(new WordRow(word));
            }
            else
            {
                row.Add(word);
            }
        }

        foreach (var row in horizontalRows)
        {
            var segment = new List<Word>();
            foreach (var word in row.Words.OrderBy(word => word.BoundingBox.Left))
            {
                if (segment.Count > 0)
                {
                    var previous = segment[^1];
                    var gap = word.BoundingBox.Left - previous.BoundingBox.Right;
                    var maximumGap = Math.Max(18, Math.Max(previous.BoundingBox.Height, word.BoundingBox.Height) * 3);
                    if (gap > maximumGap)
                    {
                        result.Add(CreateLine(segment));
                        segment.Clear();
                    }
                }

                segment.Add(word);
            }

            if (segment.Count > 0)
            {
                result.Add(CreateLine(segment));
            }
        }

        return result.OrderByDescending(line => line.Top).ThenBy(line => line.Left).ToArray();
    }

    private static PdfTextLine CreateLine(IReadOnlyList<Word> words)
    {
        var ordered = words.OrderBy(word => word.BoundingBox.Left).ToArray();
        var spans = new List<PdfTextSpan>();
        Letter? previousLetter = null;
        for (var wordIndex = 0; wordIndex < ordered.Length; wordIndex++)
        {
            var letters = ordered[wordIndex].Letters;
            if (wordIndex > 0 && NeedsSpace(ordered[wordIndex - 1], ordered[wordIndex]) && previousLetter is not null)
            {
                AppendSpan(spans, " ", previousLetter);
            }

            foreach (var letter in letters)
            {
                AppendSpan(spans, letter.Value, letter);
                previousLetter = letter;
            }
        }

        var allLetters = ordered.SelectMany(word => word.Letters).ToArray();
        var firstLetter = allLetters.First();
        return new PdfTextLine(string.Concat(spans.Select(span => span.Text)), ordered.Min(word => word.BoundingBox.Left),
            ordered.Min(word => word.BoundingBox.Bottom), ordered.Max(word => word.BoundingBox.Right), ordered.Max(word => word.BoundingBox.Top),
            firstLetter.TextOrientation, spans, allLetters.Select(letter => letter.TextSequence).Distinct().Order().ToArray());
    }

    private static void AppendSpan(List<PdfTextSpan> spans, string text, Letter styleSource)
    {
        var color = styleSource.Color.ToRGBValues();
        if (spans.LastOrDefault() is { } previous && string.Equals(previous.FontName, styleSource.FontName, StringComparison.Ordinal) &&
            Math.Abs(previous.FontSize - styleSource.PointSize) < 0.01 && previous.Color == color)
        {
            spans[^1] = previous with { Text = previous.Text + text };
            return;
        }

        spans.Add(new PdfTextSpan(text, styleSource.PointSize, styleSource.FontName, color));
    }

    private static bool NeedsSpace(Word previous, Word current)
    {
        var gap = current.BoundingBox.Left - previous.BoundingBox.Right;
        if (gap <= 0.5)
        {
            return false;
        }

        var previousLast = previous.Text.LastOrDefault();
        var currentFirst = current.Text.FirstOrDefault();
        if (IsCjk(previousLast) || IsCjk(currentFirst))
        {
            return gap > Math.Max(previous.BoundingBox.Height, current.BoundingBox.Height) * 0.45;
        }

        return true;
    }

    private static bool IsTextDrawingOperation(COperator operation) =>
        operation.OpCode.OpCodeName is OpCodeName.Tj or OpCodeName.TJ or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble;

    private static XFontStyleEx ResolveFontStyle(string? fontName)
    {
        var bold = fontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true;
        var italic = fontName?.Contains("Italic", StringComparison.OrdinalIgnoreCase) == true ||
                     fontName?.Contains("Oblique", StringComparison.OrdinalIgnoreCase) == true;
        return (bold, italic) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular
        };
    }

    private static bool ShouldTranslate(string value) => value.Any(char.IsLetter);

    private static bool IsCjk(char value) => value is >= '\u3400' and <= '\u9FFF';

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);

    private sealed class WordRow
    {
        private double _baseline;

        public WordRow(Word first)
        {
            Words.Add(first);
            _baseline = first.BoundingBox.Bottom;
        }

        public List<Word> Words { get; } = [];

        public bool CanAccept(Word word)
        {
            var rowHeight = Words.Max(item => item.BoundingBox.Height);
            var tolerance = Math.Max(2, Math.Max(rowHeight, word.BoundingBox.Height) * 0.55);
            return Math.Abs(_baseline - word.BoundingBox.Bottom) <= tolerance;
        }

        public void Add(Word word)
        {
            Words.Add(word);
            _baseline = Words.Average(item => item.BoundingBox.Bottom);
        }
    }

    private sealed class PdfTextBlockBuilder
    {
        private readonly List<PdfTextLine> _lines = [];

        public PdfTextBlockBuilder(PdfTextLine first)
        {
            _lines.Add(first);
        }

        public bool CanAppend(PdfTextLine line)
        {
            var previous = _lines[^1];
            if (previous.Orientation != TextOrientation.Horizontal || line.Orientation != TextOrientation.Horizontal)
            {
                return false;
            }

            var maximumHeight = Math.Max(previous.Height, line.Height);
            var verticalGap = previous.Bottom - line.Top;
            var isNextVisualLine = previous.Top - line.Top >= maximumHeight * 0.45;
            var leftAligned = Math.Abs(previous.Left - line.Left) <= Math.Max(18, maximumHeight * 2);
            var overlap = Math.Min(previous.Right, line.Right) - Math.Max(previous.Left, line.Left);
            var hasHorizontalRelationship = leftAligned || overlap >= Math.Min(previous.Width, line.Width) * 0.25;
            return isNextVisualLine && verticalGap <= maximumHeight * 1.35 && hasHorizontalRelationship;
        }

        public void Append(PdfTextLine line) => _lines.Add(line);

        public PdfTextBlock Build() => new(_lines.ToArray());
    }

    private sealed class PdfTextBlock
    {
        public PdfTextBlock(IReadOnlyList<PdfTextLine> lines)
        {
            Lines = lines;
            Text = string.Join('\n', lines.Select(line => line.Text));
            TextSequences = lines.SelectMany(line => line.TextSequences).Distinct().Order().ToArray();
            Top = lines.Max(line => line.Top);
            Bottom = lines.Min(line => line.Bottom);
        }

        public IReadOnlyList<PdfTextLine> Lines { get; }

        public string Text { get; }

        public IReadOnlyList<int> TextSequences { get; }

        public double Top { get; }

        public double Bottom { get; }
    }

    private sealed record PdfTextLine(
        string Text,
        double Left,
        double Bottom,
        double Right,
        double Top,
        TextOrientation Orientation,
        IReadOnlyList<PdfTextSpan> Spans,
        IReadOnlyList<int> TextSequences)
    {
        public double Width => Right - Left;

        public double Height => Top - Bottom;

        public int? TranslationId { get; set; }
    }

    private sealed record PdfTextSpan(string Text, double FontSize, string? FontName, (double Red, double Green, double Blue) Color);

    private sealed record PreparedPdfSpan(
        string Text,
        string FontFamily,
        XFontStyleEx Style,
        double FontSize,
        (double Red, double Green, double Blue) Color,
        double Width,
        double Height);

    private sealed record PreparedPdfLine(PdfTextLine Line, IReadOnlyList<PreparedPdfSpan> Spans, double Width, double Height);

    private sealed record PdfPagePlan(int PageIndex, double PageHeight, IReadOnlyList<PdfTextLine> Lines, IReadOnlyList<PdfTextBlock> Blocks);

    private sealed record PdfBlockReplacement(IReadOnlyList<int> TextSequences, CSequence Sequence);

    private sealed record TextObjectRange(int StartIndex, int EndIndex, IReadOnlyList<int> TextSequences);
}