using AITranslator.Models;

namespace AITranslator.Services;

public interface ITranslationProvider
{
    string ProviderId { get; }

    Task<TranslationResult> TranslateAsync(TranslationRequest request, AppSettings settings, string apiKey,
        CancellationToken cancellationToken = default);

    Task<CaptureTranslationResult> TranslateCaptureAsync(CaptureTranslationRequest request, AppSettings settings, string apiKey,
        CancellationToken cancellationToken = default);

    Task<DocumentTranslationResult> TranslateDocumentAsync(DocumentTranslationRequest request, AppSettings settings, string apiKey,
        CancellationToken cancellationToken = default);

    Task<LookupAnalysisResult> LookupAsync(string text, string domain, AppSettings settings, string apiKey,
        CancellationToken cancellationToken = default);
}
