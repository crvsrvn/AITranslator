using AITranslator.Models;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.TextState;
using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace AITranslator.Services;

internal sealed class PdfLayoutTranslationService
{
    private readonly TranslationOrchestrator _translator;
    private readonly OcrService _ocr;

    public PdfLayoutTranslationService(TranslationOrchestrator translator, OcrService ocr)
    {
        _translator = translator;
        _ocr = ocr;
    }

    public async Task<int> TranslateAsync(string sourcePath, string outputPath, string sourceLanguage, string targetLanguage, string domain,
        bool translateImages, IProgress<FileTranslationProgress>? progress, CancellationToken cancellationToken)
    {
        using var outputDocument = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);
        var repairedSource = RepairMalformedContent(outputDocument);
        using var sourceStream = repairedSource ? CreateRepairedSourceStream(sourcePath) : null;
        using var sourceDocument = sourceStream is null
            ? PdfPigDocument.Open(sourcePath, new UglyToad.PdfPig.ParsingOptions { SkipMissingFonts = true })
            : PdfPigDocument.Open(sourceStream, new UglyToad.PdfPig.ParsingOptions { SkipMissingFonts = true });
        if (sourceDocument.NumberOfPages != outputDocument.PageCount)
        {
            throw new InvalidDataException("PDF 页面结构不一致，无法安全替换文本。");
        }

        var imageOcrDocument = translateImages
            ? await PdfImageOcrDocument.OpenAsync(sourcePath, _ocr, cancellationToken)
            : null;
        if (imageOcrDocument is not null && imageOcrDocument.PageCount != sourceDocument.NumberOfPages)
        {
            throw new InvalidDataException("PDF 图像渲染器返回的页面数量不一致，无法安全识别图像文字。");
        }

        var pagePlans = new List<PdfPagePlan>(sourceDocument.NumberOfPages);
        var requestUnits = new List<DocumentTextUnit>();
        for (var pageIndex = 0; pageIndex < sourceDocument.NumberOfPages; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePage = sourceDocument.GetPage(pageIndex + 1);
            var lines = ExtractLines(sourcePage);
            var vectorLines = translateImages ? lines.Where(line => line.IsVisible).ToArray() : lines;
            var blocks = CreateBlocks(vectorLines);
            foreach (var line in vectorLines.Where(line => ShouldTranslate(line.Text)))
            {
                line.TranslationId = requestUnits.Count;
                requestUnits.Add(new DocumentTextUnit(line.TranslationId.Value, line.Text));
            }

            IReadOnlyList<PdfImageTextLine> imageLines = [];
            if (imageOcrDocument is not null)
            {
                progress?.Report(new FileTranslationProgress(0, 0,
                    $"正在识别图像文字（{pageIndex + 1}/{sourceDocument.NumberOfPages}）"));
                var outputPage = outputDocument.Pages[pageIndex];
                var mediaBox = outputPage.MediaBoxReadOnly;
                var cropBox = outputPage.CropBoxReadOnly;
                if (cropBox.IsZero)
                {
                    cropBox = mediaBox;
                }

                var geometry = new PdfImagePageGeometry(mediaBox.Width, mediaBox.Height, cropBox.X1 - mediaBox.X1,
                    cropBox.Y1 - mediaBox.Y1, cropBox.Width, cropBox.Height, outputPage.Rotate);
                var visibleTextRegions = lines.Where(line => line.IsVisible)
                    .Select(line => new PdfImageBounds(line.Left, sourcePage.Height - line.Top, line.Width, line.Height))
                    .Select(bounds => PdfImageOcrDocument.MapDisplayToPageBounds(bounds, geometry)).ToArray();
                imageLines = await imageOcrDocument.RecognizePageAsync((uint)pageIndex, geometry, visibleTextRegions, sourceLanguage,
                    cancellationToken);
                foreach (var line in imageLines)
                {
                    line.TranslationId = requestUnits.Count;
                    requestUnits.Add(new DocumentTextUnit(line.TranslationId.Value, line.Text));
                }
            }

            pagePlans.Add(new PdfPagePlan(pageIndex, sourcePage.Height, vectorLines, blocks, sourcePage.Operations, imageLines));
        }

        if (requestUnits.Count == 0)
        {
            throw new NotSupportedException(translateImages
                ? "PDF 中没有可翻译的文字，图像识别也未发现文字。"
                : "PDF 中没有可直接替换的文字对象；可勾选“翻译图像”识别扫描件或纯图像页面。");
        }

        var globalReferenceText = string.Join("\n\n", pagePlans.Select(plan => string.Join('\n',
            plan.Lines.Select(line => line.Text).Concat(plan.ImageLines.Select(line => line.Text)))));
        var translations = await TranslateUnitsAsync(requestUnits, sourceLanguage, targetLanguage, domain, globalReferenceText, progress,
            cancellationToken);

        progress?.Report(new FileTranslationProgress(2, 3, "正在原位替换 PDF 文字对象"));
        await Task.Yield();
        foreach (var pagePlan in pagePlans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPage = outputDocument.Pages[pagePlan.PageIndex];
            if (pagePlan.Blocks.Count > 0)
            {
                var replacements = new List<PdfBlockReplacement>(pagePlan.Blocks.Count);
                foreach (var block in pagePlan.Blocks)
                {
                    var translatedLines = block.Lines.Select(line => line.TranslationId is int translationId
                        ? DocumentTextLayout.NormalizeSingleLine(translations[translationId])
                        : line.Text).ToArray();

                    var content = CreateReplacementContent(outputDocument, outputPage, pagePlan.PageHeight, block, translatedLines);
                    replacements.Add(new PdfBlockReplacement(block.TextSequences, content));
                }

                ReplacePageTextObjects(outputPage, pagePlan.Operations, replacements, pagePlan.PageIndex + 1);
            }

            if (pagePlan.ImageLines.Count > 0)
            {
                AppendImageTranslations(outputDocument, outputPage, pagePlan.ImageLines, translations);
            }
        }

        outputDocument.Save(outputPath);
        return requestUnits.Count;
    }

    private async Task<Dictionary<int, string>> TranslateUnitsAsync(IReadOnlyList<DocumentTextUnit> requestUnits, string sourceLanguage,
        string targetLanguage, string domain, string globalReferenceText, IProgress<FileTranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var batches = CreateTranslationBatches(requestUnits);
        var translations = new Dictionary<int, string>(requestUnits.Count);
        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new FileTranslationProgress(1, 3, batches.Count == 1
                ? "正在调用翻译 API"
                : $"正在调用翻译 API（{batchIndex + 1}/{batches.Count}）"));
            await Task.Yield();
            var batch = batches[batchIndex];
            var referenceText = globalReferenceText.Length <= 24_000
                ? globalReferenceText
                : string.Join('\n', batch.Select(unit => unit.Text));
            var translated = await _translator.TranslateDocumentAsync(
                new DocumentTranslationRequest(batch, sourceLanguage, targetLanguage, domain, referenceText), cancellationToken);
            var batchTranslations = new Dictionary<int, string>();
            foreach (var translatedUnit in translated.Units)
            {
                if (!batchTranslations.TryAdd(translatedUnit.Id, translatedUnit.Text) ||
                    !translations.TryAdd(translatedUnit.Id, translatedUnit.Text))
                {
                    throw new InvalidDataException("翻译 API 返回了重复的 PDF 文本行标识。");
                }
            }

            if (batchTranslations.Count != batch.Count || batch.Any(unit => !batchTranslations.ContainsKey(unit.Id)))
            {
                throw new InvalidDataException("翻译 API 返回的 PDF 文本行数量或标识不一致。");
            }
        }

        return translations;
    }

    private static IReadOnlyList<IReadOnlyList<DocumentTextUnit>> CreateTranslationBatches(IReadOnlyList<DocumentTextUnit> units)
    {
        const int maximumUnits = 150;
        const int maximumCharacters = 12_000;
        var batches = new List<IReadOnlyList<DocumentTextUnit>>();
        var current = new List<DocumentTextUnit>();
        var characterCount = 0;
        foreach (var unit in units)
        {
            if (current.Count > 0 && (current.Count >= maximumUnits || characterCount + unit.Text.Length > maximumCharacters))
            {
                batches.Add(current.ToArray());
                current.Clear();
                characterCount = 0;
            }

            current.Add(unit);
            characterCount += unit.Text.Length;
        }

        if (current.Count > 0)
        {
            batches.Add(current.ToArray());
        }

        return batches;
    }

    private static void AppendImageTranslations(PdfDocument document, PdfPage page, IReadOnlyList<PdfImageTextLine> imageLines,
        IReadOnlyDictionary<int, string> translations)
    {
        var content = CreateImageTranslationContent(document, page, imageLines, translations);
        using var stream = new MemoryStream();
        // 旧扫描 PDF 的隐藏 OCR 层常以 Tr=3 结束；内容流之间会继承文字状态，先显式恢复可见模式。
        stream.Write("BT\n0 Tr\nET\n"u8);
        stream.Write(content);
        var targetContent = page.Contents.AppendContent();
        targetContent.CreateStream(stream.ToArray());
        targetContent.Compressed = true;
    }

    private static byte[] CreateImageTranslationContent(PdfDocument document, PdfPage targetPage,
        IReadOnlyList<PdfImageTextLine> imageLines, IReadOnlyDictionary<int, string> translations)
    {
        var temporaryPage = document.AddPage();
        temporaryPage.Width = targetPage.Width;
        temporaryPage.Height = targetPage.Height;
        try
        {
            using (var graphics = XGraphics.FromPdfPage(temporaryPage))
            {
                DrawImageTranslations(graphics, temporaryPage, imageLines, translations);
            }

            var sequence = ContentReader.ReadContent(temporaryPage).Clone();
            MergeReferencedResources(temporaryPage, targetPage, sequence);
            temporaryPage.Contents.ReplaceContent(sequence);
            return temporaryPage.Contents.CreateSingleContent().Stream.UnfilteredValue;
        }
        finally
        {
            document.Pages.Remove(temporaryPage);
        }
    }

    private static void DrawImageTranslations(XGraphics graphics, PdfPage page, IReadOnlyList<PdfImageTextLine> imageLines,
        IReadOnlyDictionary<int, string> translations)
    {
        var preparedLines = new List<(PdfImageTextLine Line, string Text, XRect Rectangle)>();
        foreach (var line in imageLines)
        {
            if (line.TranslationId is not int translationId)
            {
                continue;
            }

            var text = DocumentTextLayout.NormalizeSingleLine(translations[translationId]);
            if (text.Length == 0)
            {
                continue;
            }

            preparedLines.Add((line, text, CreateImageTranslationRectangle(page, line)));
        }

        foreach (var prepared in preparedLines)
        {
            var background = XColor.FromArgb(prepared.Line.Background.Red, prepared.Line.Background.Green, prepared.Line.Background.Blue);
            graphics.DrawRectangle(new XSolidBrush(background), prepared.Rectangle);
        }

        foreach (var prepared in preparedLines)
        {
            DrawImageTranslation(graphics, prepared.Line, prepared.Text, prepared.Rectangle);
        }
    }

    private static XRect CreateImageTranslationRectangle(PdfPage page, PdfImageTextLine line)
    {
        var thickness = Math.Max(1, Math.Min(line.Bounds.Width, line.Bounds.Height));
        var alongTextPadding = Math.Clamp(thickness * 0.25, 1, 3);
        var crossTextPadding = Math.Clamp(thickness * 0.12, 0.5, 1.5);
        var isVertical = Math.Abs(line.Angle) is > 45 and < 135;
        var horizontalPadding = isVertical ? crossTextPadding : alongTextPadding;
        var verticalPadding = isVertical ? alongTextPadding : crossTextPadding;
        var left = Math.Clamp(line.Bounds.Left - horizontalPadding, 0, page.Width.Point);
        var top = Math.Clamp(line.Bounds.Top - verticalPadding, 0, page.Height.Point);
        var right = Math.Clamp(line.Bounds.Right + horizontalPadding, left, page.Width.Point);
        var bottom = Math.Clamp(line.Bounds.Bottom + verticalPadding, top, page.Height.Point);
        return new XRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static void DrawImageTranslation(XGraphics graphics, PdfImageTextLine line, string text, XRect rectangle)
    {
        var state = graphics.Save();
        graphics.IntersectClip(rectangle);
        var center = new XPoint(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2);
        var swapsAxes = Math.Abs(line.Angle) is > 45 and < 135;
        var textRectangle = swapsAxes
            ? new XRect(center.X - rectangle.Height / 2, center.Y - rectangle.Width / 2, rectangle.Height, rectangle.Width)
            : rectangle;
        if (Math.Abs(line.Angle) >= 0.1)
        {
            graphics.RotateAtTransform(line.Angle, center);
        }

        var requestedFamily = DocumentTextLayout.ResolveFontFamily(null, text);
        var (family, _) = CreatePdfFont(requestedFamily, Math.Max(1, textRectangle.Height), XFontStyleEx.Regular);
        var layout = CreateImageTextLayout(graphics, text, family, textRectangle);
        var font = new XFont(family, layout.FontSize, XFontStyleEx.Regular);
        var foreground = XColor.FromArgb(line.Foreground.Red, line.Foreground.Green, line.Foreground.Blue);
        var y = textRectangle.Top + Math.Max(0, (textRectangle.Height - layout.LineHeight * layout.Lines.Count) / 2);
        foreach (var value in layout.Lines)
        {
            graphics.DrawString(value, font, new XSolidBrush(foreground),
                new XRect(textRectangle.Left, y, textRectangle.Width, layout.LineHeight), XStringFormats.Center);
            y += layout.LineHeight;
        }

        graphics.Restore(state);
    }

    private static ImageTextLayout CreateImageTextLayout(XGraphics graphics, string text, string fontFamily, XRect bounds)
    {
        var minimum = 1d;
        var maximum = Math.Clamp(bounds.Height * 1.2, minimum, 200);
        var best = MeasureImageText(graphics, text, fontFamily, minimum, bounds.Width, bounds.Height);
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var candidateSize = (minimum + maximum) / 2;
            var candidate = MeasureImageText(graphics, text, fontFamily, candidateSize, bounds.Width, bounds.Height);
            if (candidate.Fits)
            {
                best = candidate;
                minimum = candidateSize;
            }
            else
            {
                maximum = candidateSize;
            }
        }

        return best;
    }

    private static ImageTextLayout MeasureImageText(XGraphics graphics, string text, string fontFamily, double fontSize, double width,
        double height)
    {
        var font = new XFont(fontFamily, fontSize, XFontStyleEx.Regular);
        var lines = WrapImageText(graphics, text, font, Math.Max(1, width));
        var lineHeight = Math.Max(1, graphics.MeasureString("Ag国", font).Height * 1.05);
        var fits = lines.Count * lineHeight <= height + 0.01 &&
                   lines.All(line => graphics.MeasureString(line, font).Width <= width + 0.01);
        return new ImageTextLayout(fontSize, lines, lineHeight, fits);
    }

    private static IReadOnlyList<string> WrapImageText(XGraphics graphics, string text, XFont font, double maximumWidth)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (Match match in Regex.Matches(text, @"\s+|[\u3400-\u9FFF]|[^\s\u3400-\u9FFF]+"))
        {
            var token = match.Value;
            if (string.IsNullOrWhiteSpace(token))
            {
                if (current.Length > 0 && graphics.MeasureString(current + " ", font).Width <= maximumWidth)
                {
                    current.Append(' ');
                }

                continue;
            }

            if (graphics.MeasureString(current + token, font).Width <= maximumWidth)
            {
                current.Append(token);
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            while (token.Length > 0 && graphics.MeasureString(token, font).Width > maximumWidth)
            {
                var length = 1;
                while (length < token.Length && graphics.MeasureString(token[..(length + 1)], font).Width <= maximumWidth)
                {
                    length++;
                }

                lines.Add(token[..length]);
                token = token[length..];
            }

            current.Append(token);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString().TrimEnd());
        }

        return lines.Count == 0 ? [text] : lines;
    }

    private static byte[] CreateReplacementContent(PdfDocument document, PdfPage targetPage, double pageHeight, PdfTextBlock block,
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
            MergeReferencedResources(temporaryPage, targetPage, sequence);
            temporaryPage.Contents.ReplaceContent(sequence);
            return temporaryPage.Contents.CreateSingleContent().Stream.UnfilteredValue;
        }
        finally
        {
            document.Pages.Remove(temporaryPage);
        }
    }

    private static void MergeReferencedResources(PdfPage sourcePage, PdfPage targetPage, CSequence sequence)
    {
        var sourceFonts = sourcePage.Resources.Elements.GetDictionary("/Font");
        if (sourceFonts is null)
        {
            throw new InvalidDataException("PDF 译文字体资源未生成。");
        }

        var remappedFontNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var remappedExtGStateNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var operation in sequence.OfType<COperator>())
        {
            if (operation.OpCode.OpCodeName == OpCodeName.Tf)
            {
                var fontName = GetResourceNameOperand(operation, "PDF 译文字体操作无效。");
                if (!remappedFontNames.TryGetValue(fontName.Name, out var targetName))
                {
                    var font = sourceFonts.Elements.GetObject(fontName.Name) as PdfFont ??
                               throw new InvalidDataException("PDF 译文字体资源无法读取。");
                    targetName = targetPage.Resources.AddFont(font);
                    remappedFontNames[fontName.Name] = targetName;
                }

                fontName.Name = targetName;
                continue;
            }

            if (operation.OpCode.OpCodeName == OpCodeName.gs)
            {
                var extGStateName = GetResourceNameOperand(operation, "PDF 译文图形状态操作无效。");
                if (!remappedExtGStateNames.TryGetValue(extGStateName.Name, out var targetName))
                {
                    var sourceExtGStates = sourcePage.Resources.Elements.GetDictionary("/ExtGState");
                    var extGState = sourceExtGStates?.Elements.GetObject(extGStateName.Name) as PdfExtGState ??
                                    throw new InvalidDataException("PDF 译文图形状态资源无法读取。");
                    targetName = targetPage.Resources.AddExtGState(extGState);
                    remappedExtGStateNames[extGStateName.Name] = targetName;
                }

                extGStateName.Name = targetName;
            }
        }
    }

    private static CName GetResourceNameOperand(COperator operation, string errorMessage)
    {
        if (operation.Operands.Count == 0 || operation.Operands[0] is not CName resourceName ||
            string.IsNullOrWhiteSpace(resourceName.Name))
        {
            throw new InvalidDataException(errorMessage);
        }

        return resourceName;
    }

    private static void ReplacePageTextObjects(PdfPage page, IReadOnlyList<IGraphicsStateOperation> operations,
        IReadOnlyList<PdfBlockReplacement> replacements,
        int pageNumber)
    {
        var replacementSequences = new HashSet<int>();
        var mergedReplacements = MergeOverlappingReplacements(replacements);
        foreach (var replacement in mergedReplacements)
        {
            if (replacement.TextSequences.Count == 0 || replacement.TextSequences.Any(sequence => !replacementSequences.Add(sequence)))
            {
                throw new NotSupportedException($"PDF 第 {pageNumber} 页的文字绘制操作相互交叠，无法安全原位替换。");
            }
        }

        // PDFsharp 重写内容序列会丢失内联图片数据，因此用 PdfPig 的图形操作重建原页面。
        using var stream = new MemoryStream();
        stream.Write("q\n"u8);
        var hiddenSequences = new HashSet<int>();
        var renderingModeStack = new Stack<int>();
        var renderingMode = 0;
        var textSequence = 1;
        var invisibleMode = new SetTextRenderingMode(3);
        var invisibleClippingMode = new SetTextRenderingMode(7);
        foreach (var operation in operations)
        {
            if (IsTextDrawingOperation(operation))
            {
                if (replacementSequences.Contains(textSequence))
                {
                    (renderingMode >= 4 ? invisibleClippingMode : invisibleMode).Write(stream);
                    operation.Write(stream);
                    new SetTextRenderingMode(renderingMode).Write(stream);
                    hiddenSequences.Add(textSequence);
                }
                else
                {
                    operation.Write(stream);
                }

                textSequence++;
            }
            else
            {
                operation.Write(stream);
            }

            if (operation.Operator == "q")
            {
                renderingModeStack.Push(renderingMode);
            }
            else if (operation.Operator == "Q")
            {
                renderingMode = renderingModeStack.Count == 0 ? 0 : renderingModeStack.Pop();
            }
            else if (operation is SetTextRenderingMode setTextRenderingMode)
            {
                renderingMode = (int)setTextRenderingMode.Mode;
            }
        }

        if (!replacementSequences.SetEquals(hiddenSequences))
        {
            throw new NotSupportedException($"PDF 第 {pageNumber} 页的文字位于嵌套表单或不受支持的内容流中，无法安全原位替换。");
        }

        stream.Write("\nQ\n"u8);
        foreach (var replacement in mergedReplacements)
        {
            stream.Write(replacement.Content);
            stream.WriteByte((byte)'\n');
        }

        page.Contents.Elements.Clear();
        var content = page.Contents.AppendContent();
        content.CreateStream(stream.ToArray());
        content.Compressed = true;
    }

    private static IReadOnlyList<PdfBlockReplacement> MergeOverlappingReplacements(IReadOnlyList<PdfBlockReplacement> replacements)
    {
        var groups = new List<PdfBlockReplacementBuilder>();
        foreach (var replacement in replacements)
        {
            var matchingIndexes = groups.Select((group, index) => (Group: group, Index: index))
                .Where(item => item.Group.TextSequences.Overlaps(replacement.TextSequences)).Select(item => item.Index).ToArray();
            if (matchingIndexes.Length == 0)
            {
                groups.Add(new PdfBlockReplacementBuilder(replacement));
                continue;
            }

            var target = groups[matchingIndexes[0]];
            foreach (var matchingIndex in matchingIndexes.Skip(1))
            {
                target.Append(groups[matchingIndex]);
            }

            target.Append(replacement);
            for (var index = matchingIndexes.Length - 1; index >= 1; index--)
            {
                groups.RemoveAt(matchingIndexes[index]);
            }
        }

        return groups.Select(group => group.Build()).ToArray();
    }

    private static MemoryStream CreateRepairedSourceStream(string sourcePath)
    {
        using var document = PdfReader.Open(sourcePath, PdfDocumentOpenMode.Modify);
        RepairMalformedContent(document);
        var stream = new MemoryStream();
        document.Save(stream, false);
        stream.Position = 0;
        return stream;
    }

    private static bool RepairMalformedContent(PdfDocument document)
    {
        var repaired = false;
        for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
        {
            var page = document.Pages[pageIndex];
            var content = ContentReader.ReadContent(page);
            var rewritten = new CSequence();
            var fontStack = new Stack<string?>();
            var remappedMissingExtGStates = new Dictionary<string, string>(StringComparer.Ordinal);
            var containsInlineImage = content.OfType<COperator>()
                .Any(operation => operation.OpCode.OpCodeName == OpCodeName.BI);
            string? currentFont = null;
            var pageRepaired = false;

            foreach (var item in content)
            {
                if (item is not COperator operation)
                {
                    rewritten.Add(item);
                    continue;
                }

                if (operation.OpCode.OpCodeName == OpCodeName.q)
                {
                    fontStack.Push(currentFont);
                }
                else if (operation.OpCode.OpCodeName == OpCodeName.Q)
                {
                    currentFont = fontStack.Count == 0 ? null : fontStack.Pop();
                }
                else if (operation.OpCode.OpCodeName == OpCodeName.Tf)
                {
                    currentFont = operation.Operands.Count > 0 && operation.Operands[0] is CName fontName &&
                                  HasResource(page, "/Font", fontName.Name)
                        ? fontName.Name
                        : null;
                }
                else if (operation.OpCode.OpCodeName == OpCodeName.gs && operation.Operands.Count > 0 &&
                         operation.Operands[0] is CName extGStateName && !HasResource(page, "/ExtGState", extGStateName.Name))
                {
                    if (string.IsNullOrWhiteSpace(extGStateName.Name))
                    {
                        throw new NotSupportedException($"PDF 第 {pageIndex + 1} 页包含无效的图形状态资源名，无法安全翻译。");
                    }

                    if (!remappedMissingExtGStates.TryGetValue(extGStateName.Name, out var targetName))
                    {
                        var extGState = new PdfExtGState(document) { StrokeAlpha = 1, NonStrokeAlpha = 1 };
                        targetName = page.Resources.AddExtGState(extGState);
                        remappedMissingExtGStates[extGStateName.Name] = targetName;
                    }

                    extGStateName.Name = targetName;
                    pageRepaired = true;
                }

                if (IsTextDrawingOperation(operation) && currentFont is null)
                {
                    if (!DrawsOnlyWhitespace(operation))
                    {
                        throw new NotSupportedException($"PDF 第 {pageIndex + 1} 页包含未指定字体的非空白文字，无法安全翻译。");
                    }

                    pageRepaired = true;
                    continue;
                }

                rewritten.Add(item);
            }

            if (pageRepaired)
            {
                if (containsInlineImage)
                {
                    throw new NotSupportedException($"PDF 第 {pageIndex + 1} 页同时包含畸形资源与内联图片，无法安全修复后翻译。");
                }

                page.Contents.ReplaceContent(rewritten);
                repaired = true;
            }
        }

        return repaired;
    }

    private static bool HasResource(PdfPage page, string resourceType, string? resourceName) =>
        !string.IsNullOrWhiteSpace(resourceName) &&
        page.Resources.Elements.GetDictionary(resourceType)?.Elements.ContainsKey(resourceName) == true;

    private static bool DrawsOnlyWhitespace(COperator operation)
    {
        var values = EnumerateStrings(operation.Operands).ToArray();
        return values.Length > 0 && values.All(value => value.All(char.IsWhiteSpace));
    }

    private static IEnumerable<string> EnumerateStrings(CSequence sequence)
    {
        foreach (var item in sequence)
        {
            if (item is CString text)
            {
                yield return text.Value;
            }
            else if (item is CArray array)
            {
                foreach (var value in EnumerateStrings(array))
                {
                    yield return value;
                }
            }
        }
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
        var isVisible = allLetters.Any(letter => letter.RenderingMode is not TextRenderingMode.Neither and not TextRenderingMode.NeitherClip);
        return new PdfTextLine(string.Concat(spans.Select(span => span.Text)), ordered.Min(word => word.BoundingBox.Left),
            ordered.Min(word => word.BoundingBox.Bottom), ordered.Max(word => word.BoundingBox.Right), ordered.Max(word => word.BoundingBox.Top),
            firstLetter.TextOrientation, spans, allLetters.Select(letter => letter.TextSequence).Distinct().Order().ToArray(), isVisible);
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

    private static bool IsTextDrawingOperation(IGraphicsStateOperation operation) =>
        operation.Operator is "Tj" or "TJ" or "'" or "\"";

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
        IReadOnlyList<int> TextSequences,
        bool IsVisible)
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

    private sealed record ImageTextLayout(double FontSize, IReadOnlyList<string> Lines, double LineHeight, bool Fits);

    private sealed record PdfPagePlan(
        int PageIndex,
        double PageHeight,
        IReadOnlyList<PdfTextLine> Lines,
        IReadOnlyList<PdfTextBlock> Blocks,
        IReadOnlyList<IGraphicsStateOperation> Operations,
        IReadOnlyList<PdfImageTextLine> ImageLines);

    private sealed record PdfBlockReplacement(IReadOnlyList<int> TextSequences, byte[] Content);

    private sealed class PdfBlockReplacementBuilder
    {
        public PdfBlockReplacementBuilder(PdfBlockReplacement replacement)
        {
            Append(replacement);
        }

        public HashSet<int> TextSequences { get; } = [];

        private List<byte[]> ContentParts { get; } = [];

        public void Append(PdfBlockReplacement replacement)
        {
            TextSequences.UnionWith(replacement.TextSequences);
            ContentParts.Add(replacement.Content);
        }

        public void Append(PdfBlockReplacementBuilder replacement)
        {
            TextSequences.UnionWith(replacement.TextSequences);
            ContentParts.AddRange(replacement.ContentParts);
        }

        public PdfBlockReplacement Build()
        {
            using var stream = new MemoryStream();
            foreach (var content in ContentParts)
            {
                stream.Write(content);
                stream.WriteByte((byte)'\n');
            }

            return new PdfBlockReplacement(TextSequences.Order().ToArray(), stream.ToArray());
        }
    }
}
