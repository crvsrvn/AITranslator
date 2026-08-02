using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using AITranslator.Helpers;
using AITranslator.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AITranslator.Services;

public sealed class DocumentTranslationService
{
    private readonly TranslationOrchestrator _translator;
    private readonly PdfLayoutTranslationService _pdfTranslator;

    public DocumentTranslationService(TranslationOrchestrator translator)
    {
        _translator = translator;
        _pdfTranslator = new PdfLayoutTranslationService(translator);
    }

    public static IReadOnlyList<string> SupportedExtensions { get; } = [".pdf", ".docx", ".pptx", ".xlsx"];

    public async Task<FileTranslationReport> TranslateAsync(string sourcePath, string sourceLanguage, string targetLanguage, string domain,
        IProgress<FileTranslationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到待翻译文件。", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("仅支持 PDF、DOCX、PPTX 和 XLSX 文件。");
        }

        progress?.Report(new FileTranslationProgress(0, 3, "正在提取文件文本"));
        await Task.Yield();

        var report = extension switch
        {
            ".docx" => await TranslateWordAsync(sourcePath, sourceLanguage, targetLanguage, domain, progress, cancellationToken),
            ".pptx" => await TranslatePresentationAsync(sourcePath, sourceLanguage, targetLanguage, domain, progress, cancellationToken),
            ".xlsx" => await TranslateWorkbookAsync(sourcePath, sourceLanguage, targetLanguage, domain, progress, cancellationToken),
            ".pdf" => await TranslatePdfAsync(sourcePath, sourceLanguage, targetLanguage, domain, progress, cancellationToken),
            _ => throw new NotSupportedException()
        };

        progress?.Report(new FileTranslationProgress(3, 3, "完成"));
        await Task.Yield();
        return report;
    }

    private async Task<FileTranslationReport> TranslateWordAsync(string sourcePath, string sourceLanguage, string targetLanguage, string domain,
        IProgress<FileTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        var outputPath = CreateOutputPath(sourcePath, ".docx");
        File.Copy(sourcePath, outputPath);

        using var document = WordprocessingDocument.Open(outputPath, true);
        var mainPart = document.MainDocumentPart ?? throw new InvalidDataException("Word 文件缺少正文结构。");
        var units = new List<TextUnit>();
        AddWordUnits(units, mainPart.Document.Descendants<W.Paragraph>());
        foreach (var headerPart in mainPart.HeaderParts)
        {
            AddWordUnits(units, headerPart.Header.Descendants<W.Paragraph>());
        }

        foreach (var footerPart in mainPart.FooterParts)
        {
            AddWordUnits(units, footerPart.Footer.Descendants<W.Paragraph>());
        }

        if (mainPart.FootnotesPart?.Footnotes is { } footnotes)
        {
            AddWordUnits(units, footnotes.Descendants<W.Paragraph>());
        }

        if (mainPart.EndnotesPart?.Endnotes is { } endnotes)
        {
            AddWordUnits(units, endnotes.Descendants<W.Paragraph>());
        }

        await ProcessUnitsAsync(units, sourceLanguage, targetLanguage, domain, progress, cancellationToken);
        mainPart.Document.Save();
        foreach (var headerPart in mainPart.HeaderParts)
        {
            headerPart.Header.Save();
        }

        foreach (var footerPart in mainPart.FooterParts)
        {
            footerPart.Footer.Save();
        }

        mainPart.FootnotesPart?.Footnotes?.Save();
        mainPart.EndnotesPart?.Endnotes?.Save();
        return new FileTranslationReport(sourcePath, outputPath, units.Count);
    }

    private async Task<FileTranslationReport> TranslatePresentationAsync(string sourcePath, string sourceLanguage, string targetLanguage,
        string domain, IProgress<FileTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        var outputPath = CreateOutputPath(sourcePath, ".pptx");
        File.Copy(sourcePath, outputPath);

        using var document = PresentationDocument.Open(outputPath, true);
        var presentationPart = document.PresentationPart ?? throw new InvalidDataException("PowerPoint 文件缺少演示文稿结构。");
        var slideParts = presentationPart.SlideParts.ToArray();
        var units = new List<TextUnit>();
        var textBlocks = new List<PresentationTextBlock>();
        foreach (var slidePart in slideParts)
        {
            AddPresentationUnits(units, textBlocks, slidePart.Slide.Descendants<P.TextBody>());
            if (slidePart.NotesSlidePart?.NotesSlide is { } notesSlide)
            {
                AddPresentationUnits(units, textBlocks, notesSlide.Descendants<P.TextBody>());
            }
        }

        await ProcessUnitsAsync(units, sourceLanguage, targetLanguage, domain, progress, cancellationToken);
        foreach (var textBlock in textBlocks)
        {
            textBlock.ApplyLayout();
        }

        foreach (var slidePart in slideParts)
        {
            slidePart.Slide.Save();
            slidePart.NotesSlidePart?.NotesSlide.Save();
        }

        return new FileTranslationReport(sourcePath, outputPath, units.Count);
    }

    private async Task<FileTranslationReport> TranslateWorkbookAsync(string sourcePath, string sourceLanguage, string targetLanguage, string domain,
        IProgress<FileTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        var outputPath = CreateOutputPath(sourcePath, ".xlsx");
        File.Copy(sourcePath, outputPath);

        using var document = SpreadsheetDocument.Open(outputPath, true);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("Excel 文件缺少工作簿结构。");
        var units = new List<TextUnit>();

        var sharedStringTable = workbookPart.SharedStringTablePart?.SharedStringTable;
        if (sharedStringTable is not null)
        {
            units.AddRange(sharedStringTable.Elements<S.SharedStringItem>().Select(item => CreateTextUnit(item.Descendants<S.Text>().ToArray()))
                .Where(unit => unit is not null).Select(unit => unit!));
        }

        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            foreach (var cell in worksheetPart.Worksheet.Descendants<S.Cell>())
            {
                if (cell.CellFormula is not null || cell.DataType is null)
                {
                    continue;
                }

                if (cell.DataType.Value == S.CellValues.InlineString && cell.InlineString is not null)
                {
                    var unit = CreateTextUnit(cell.InlineString.Descendants<S.Text>().ToArray());
                    if (unit is not null)
                    {
                        units.Add(unit);
                    }
                }
                else if (cell.DataType.Value == S.CellValues.String && cell.CellValue is not null)
                {
                    var originalText = cell.CellValue.Text;
                    if (!string.IsNullOrWhiteSpace(originalText))
                    {
                        units.Add(new TextUnit(originalText, value => cell.CellValue.Text = value));
                    }
                }
            }
        }

        await ProcessUnitsAsync(units, sourceLanguage, targetLanguage, domain, progress, cancellationToken);

        sharedStringTable?.Save();
        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            worksheetPart.Worksheet.Save();
        }

        workbookPart.Workbook.Save();
        return new FileTranslationReport(sourcePath, outputPath, units.Count);
    }

    private async Task<FileTranslationReport> TranslatePdfAsync(string sourcePath, string sourceLanguage, string targetLanguage, string domain,
        IProgress<FileTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        var outputPath = CreateOutputPath(sourcePath, ".pdf");
        var translatedLineCount = await _pdfTranslator.TranslateAsync(sourcePath, outputPath, sourceLanguage, targetLanguage, domain,
            progress, cancellationToken);
        return new FileTranslationReport(sourcePath, outputPath, translatedLineCount);
    }

    private async Task ProcessUnitsAsync(IReadOnlyList<TextUnit> units, string sourceLanguage, string targetLanguage, string domain,
        IProgress<FileTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        if (units.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var requestUnits = new List<DocumentTextUnit>();
        var structuredUnits = units.Select(unit => CreateStructuredTextUnit(unit, requestUnits)).ToArray();
        if (requestUnits.Count == 0)
        {
            return;
        }

        progress?.Report(new FileTranslationProgress(1, 3, "正在调用翻译 API"));
        await Task.Yield();
        var globalReferenceText = string.Join('\n', units.Select(unit => unit.Text));
        var result = await _translator.TranslateDocumentAsync(
            new DocumentTranslationRequest(requestUnits, sourceLanguage, targetLanguage, domain, globalReferenceText), cancellationToken);
        var translations = new Dictionary<int, string>();
        foreach (var translatedUnit in result.Units)
        {
            if (!translations.TryAdd(translatedUnit.Id, translatedUnit.Text))
            {
                throw new InvalidDataException("翻译 API 返回了重复的文件文本单元标识。");
            }
        }

        if (translations.Count != requestUnits.Count || requestUnits.Any(unit => !translations.ContainsKey(unit.Id)))
        {
            throw new InvalidDataException("翻译 API 返回的文件文本单元数量或标识不一致。");
        }

        progress?.Report(new FileTranslationProgress(2, 3, "正在按原结构写回译文"));
        await Task.Yield();
        foreach (var structuredUnit in structuredUnits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            structuredUnit.Apply(translations);
        }
    }

    private static StructuredTextUnit CreateStructuredTextUnit(TextUnit unit, List<DocumentTextUnit> requestUnits)
    {
        var parts = new List<StructuredTextPart>();
        var lineStart = 0;
        for (var index = 0; index < unit.Text.Length; index++)
        {
            if (unit.Text[index] is not ('\r' or '\n'))
            {
                continue;
            }

            var lineEndingLength = unit.Text[index] == '\r' && index + 1 < unit.Text.Length && unit.Text[index + 1] == '\n' ? 2 : 1;
            parts.Add(CreateStructuredTextPart(unit.Text[lineStart..index], unit.Text.Substring(index, lineEndingLength), requestUnits));
            index += lineEndingLength - 1;
            lineStart = index + 1;
        }

        parts.Add(CreateStructuredTextPart(unit.Text[lineStart..], string.Empty, requestUnits));
        return new StructuredTextUnit(unit, parts);
    }

    private static StructuredTextPart CreateStructuredTextPart(string text, string lineEnding, List<DocumentTextUnit> requestUnits)
    {
        if (!text.Any(char.IsLetter))
        {
            return new StructuredTextPart(text, lineEnding, null);
        }

        var translationId = requestUnits.Count;
        requestUnits.Add(new DocumentTextUnit(translationId, text));
        return new StructuredTextPart(text, lineEnding, translationId);
    }

    private static TextUnit? CreateTextUnit<TText>(IReadOnlyList<TText> textNodes) where TText : OpenXmlLeafTextElement
    {
        if (textNodes.Count == 0)
        {
            return null;
        }

        var originalText = string.Concat(textNodes.Select(node => node.Text));
        if (string.IsNullOrWhiteSpace(originalText) || !originalText.Any(char.IsLetter))
        {
            return null;
        }

        var sourceLengths = textNodes.Select(node => node.Text.Length).ToArray();
        return new TextUnit(originalText, value =>
        {
            var values = DocumentTextLayout.DistributeText(value, sourceLengths);
            for (var index = 0; index < textNodes.Count; index++)
            {
                textNodes[index].Text = values[index];
                textNodes[index].SetAttribute(new OpenXmlAttribute("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve"));
            }
        });
    }

    internal static string[] DistributeText(string value, IReadOnlyList<int> sourceLengths) =>
        DocumentTextLayout.DistributeText(value, sourceLengths);

    private static void AddWordUnits(List<TextUnit> units, IEnumerable<W.Paragraph> paragraphs)
    {
        units.AddRange(paragraphs.Select(paragraph => CreateTextUnit(paragraph.Descendants<W.Text>().ToArray())).Where(unit => unit is not null)
            .Select(unit => unit!));
    }

    private static void AddPresentationUnits(List<TextUnit> units, List<PresentationTextBlock> textBlocks, IEnumerable<P.TextBody> textBodies)
    {
        foreach (var textBody in textBodies)
        {
            var paragraphs = textBody.Elements<A.Paragraph>().Where(paragraph => paragraph.Descendants<A.Text>()
                .Any(text => !string.IsNullOrWhiteSpace(text.Text))).ToArray();
            if (paragraphs.Length == 0)
            {
                continue;
            }

            foreach (var paragraph in paragraphs)
            {
                var unit = CreateTextUnit(paragraph.Descendants<A.Text>().ToArray());
                if (unit is not null)
                {
                    units.Add(unit);
                }
            }

            textBlocks.Add(new PresentationTextBlock(textBody, paragraphs));
        }
    }

    private static string CreateOutputPath(string sourcePath, string outputExtension)
    {
        var directory = Path.GetDirectoryName(sourcePath) ?? throw new InvalidOperationException("无法确定源文件目录。");
        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(directory, $"{fileName}.translated{outputExtension}");
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        return Path.Combine(directory, $"{fileName}.translated.{DateTime.Now:yyyyMMdd-HHmmss}{outputExtension}");
    }

    private sealed class PresentationTextBlock
    {
        private const double EmuPerPoint = 12_700;
        private readonly double _availableWidth;
        private readonly PresentationParagraphLayout[] _paragraphs;

        public PresentationTextBlock(P.TextBody textBody, IReadOnlyList<A.Paragraph> paragraphs)
        {
            _availableWidth = GetAvailableWidth(textBody);
            _paragraphs = paragraphs.Select(paragraph => new PresentationParagraphLayout(paragraph)).ToArray();
        }

        public void ApplyLayout()
        {
            foreach (var paragraph in _paragraphs)
            {
                paragraph.PrepareTargetMetrics();
            }

            var activeParagraphs = _paragraphs.Where(paragraph => paragraph.Runs.Count > 0).ToArray();
            if (activeParagraphs.Length == 0)
            {
                return;
            }

            var isMultiline = activeParagraphs.Length > 1 || activeParagraphs.Any(paragraph => paragraph.ExplicitLineCount > 1 ||
                                                                                               double.IsFinite(_availableWidth) &&
                                                                                               paragraph.SourceWidth > _availableWidth);
            double scale;
            if (!isMultiline)
            {
                var paragraph = activeParagraphs[0];
                scale = paragraph.TargetWidth <= 0 ? 1 : paragraph.SourceWidth / paragraph.TargetWidth;
            }
            else
            {
                var sourceHeight = activeParagraphs.Sum(paragraph => paragraph.EstimateSourceHeight(_availableWidth));
                var targetHeight = Math.Max(1, sourceHeight * 0.9);
                scale = FindBestHeightScale(activeParagraphs, _availableWidth, targetHeight, sourceHeight);
            }

            scale = Math.Clamp(scale, 0.05, 20);
            foreach (var paragraph in activeParagraphs)
            {
                paragraph.ApplyScale(scale);
            }
        }

        private static double FindBestHeightScale(IReadOnlyList<PresentationParagraphLayout> paragraphs, double availableWidth, double targetHeight,
            double sourceHeight)
        {
            var candidates = new List<double> { 1 };
            var scale = targetHeight / Math.Max(1, paragraphs.Sum(paragraph => paragraph.EstimateTargetHeight(availableWidth, 1)));
            for (var iteration = 0; iteration < 10; iteration++)
            {
                scale = Math.Clamp(scale, 0.05, 20);
                candidates.Add(scale);
                var height = paragraphs.Sum(paragraph => paragraph.EstimateTargetHeight(availableWidth, scale));
                scale *= targetHeight / Math.Max(1, height);
            }

            return candidates.OrderBy(candidate =>
            {
                var ratio = paragraphs.Sum(paragraph => paragraph.EstimateTargetHeight(availableWidth, candidate)) / Math.Max(1, sourceHeight);
                var outsidePenalty = ratio < 0.8 ? 0.8 - ratio : ratio > 1 ? ratio - 1 : 0;
                return outsidePenalty * 10 + Math.Abs(ratio - 0.9);
            }).First();
        }

        private static double GetAvailableWidth(P.TextBody textBody)
        {
            if (textBody.Parent is not P.Shape shape || shape.ShapeProperties?.Transform2D?.Extents?.Cx?.Value is not long widthEmu)
            {
                return double.PositiveInfinity;
            }

            var bodyProperties = textBody.BodyProperties;
            var leftInset = bodyProperties?.LeftInset?.Value ?? 91_440;
            var rightInset = bodyProperties?.RightInset?.Value ?? 91_440;
            return Math.Max(1, (widthEmu - leftInset - rightInset) / EmuPerPoint);
        }
    }

    private sealed class PresentationParagraphLayout
    {
        public PresentationParagraphLayout(A.Paragraph paragraph)
        {
            var defaultProperties = paragraph.GetFirstChild<A.EndParagraphRunProperties>();
            var defaultSize = defaultProperties?.FontSize?.Value ?? 1_800;
            var defaultFamily = defaultProperties?.GetFirstChild<A.LatinFont>()?.Typeface?.Value ?? "Arial";
            Runs = paragraph.Elements<A.Run>().Select(run => new PresentationRunLayout(run, defaultSize, defaultFamily))
                .Where(run => run.OriginalText.Length > 0).ToArray();
            ExplicitLineCount = paragraph.Descendants<A.Break>().Count() + 1;
        }

        public IReadOnlyList<PresentationRunLayout> Runs { get; }

        public int ExplicitLineCount { get; }

        public double SourceWidth => Runs.Sum(run => run.SourceWidth);

        public double TargetWidth => Runs.Sum(run => run.TargetWidth);

        public void PrepareTargetMetrics()
        {
            foreach (var run in Runs)
            {
                run.PrepareTargetMetrics();
            }
        }

        public double EstimateSourceHeight(double availableWidth)
        {
            var lineCount = EstimateLineCount(SourceWidth, availableWidth);
            return Math.Max(1, Runs.Max(run => run.OriginalFontSize) * 1.2 * lineCount);
        }

        public double EstimateTargetHeight(double availableWidth, double scale)
        {
            var lineCount = EstimateLineCount(TargetWidth * scale, availableWidth);
            return Math.Max(1, Runs.Max(run => run.OriginalFontSize) * scale * 1.2 * lineCount);
        }

        public void ApplyScale(double scale)
        {
            foreach (var run in Runs)
            {
                run.ApplyScale(scale);
            }
        }

        private int EstimateLineCount(double width, double availableWidth)
        {
            var wrappedLineCount = double.IsFinite(availableWidth) ? Math.Max(1, (int)Math.Ceiling(width / availableWidth)) : 1;
            return Math.Max(ExplicitLineCount, wrappedLineCount);
        }
    }

    private sealed class PresentationRunLayout
    {
        private readonly A.Run _run;
        private readonly string _requestedFamily;
        private readonly System.Drawing.FontStyle _style;
        private string _targetFamily = string.Empty;

        public PresentationRunLayout(A.Run run, int defaultFontSize, string defaultFamily)
        {
            _run = run;
            var properties = run.RunProperties;
            OriginalText = string.Concat(run.Descendants<A.Text>().Select(text => text.Text));
            OriginalFontSize = (properties?.FontSize?.Value ?? defaultFontSize) / 100d;
            _requestedFamily = properties?.GetFirstChild<A.LatinFont>()?.Typeface?.Value ?? defaultFamily;
            _style = DocumentTextLayout.ToFontStyle(properties?.Bold?.Value == true, properties?.Italic?.Value == true);
        }

        public string OriginalText { get; }

        public double OriginalFontSize { get; }

        public double SourceWidth { get; private set; }

        public double TargetWidth { get; private set; }

        public void PrepareTargetMetrics()
        {
            var currentText = string.Concat(_run.Descendants<A.Text>().Select(text => text.Text));
            var sourceFamily = DocumentTextLayout.ResolveFontFamily(_requestedFamily, OriginalText);
            _targetFamily = DocumentTextLayout.ResolveFontFamily(_requestedFamily, currentText);
            SourceWidth = DocumentTextLayout.MeasureText(OriginalText, sourceFamily, (float)OriginalFontSize, _style).Width;
            TargetWidth = DocumentTextLayout.MeasureText(currentText, _targetFamily, (float)OriginalFontSize, _style).Width;
        }

        public void ApplyScale(double scale)
        {
            var properties = _run.RunProperties ?? new A.RunProperties();
            if (_run.RunProperties is null)
            {
                _run.RunProperties = properties;
            }

            properties.FontSize = Math.Clamp((int)Math.Round(OriginalFontSize * scale * 100), 100, 40_000);
            var currentText = string.Concat(_run.Descendants<A.Text>().Select(text => text.Text));
            if (currentText.Any(character => character is >= '\u3400' and <= '\u9FFF'))
            {
                var eastAsianFont = properties.GetFirstChild<A.EastAsianFont>();
                if (eastAsianFont is null)
                {
                    properties.AddChild(new A.EastAsianFont { Typeface = _targetFamily }, true);
                }
                else
                {
                    eastAsianFont.Typeface = _targetFamily;
                }
            }
        }
    }

    private sealed record TextUnit(string Text, Action<string> Apply);

    private sealed record StructuredTextPart(string Text, string LineEnding, int? TranslationId);

    private sealed record StructuredTextUnit(TextUnit Unit, IReadOnlyList<StructuredTextPart> Parts)
    {
        public void Apply(IReadOnlyDictionary<int, string> translations)
        {
            var value = string.Concat(Parts.Select(part =>
                (part.TranslationId is int translationId ? DocumentTextLayout.NormalizeSingleLine(translations[translationId]) : part.Text) +
                part.LineEnding));
            Unit.Apply(value);
        }
    }
}