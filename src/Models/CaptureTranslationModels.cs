using Windows.Foundation;

namespace AITranslator.Models;

public sealed record OcrTextLine(string Text, Rect Bounds);

public sealed record OcrCaptureResult(string Text, IReadOnlyList<OcrTextLine> Lines);

public sealed record CaptureTranslationRequest(
    IReadOnlyList<string> Lines,
    string SourceLanguage,
    string TargetLanguage);

public sealed record CaptureTranslationResult(
    IReadOnlyList<string> Lines,
    string Provider,
    bool FromCache = false);
