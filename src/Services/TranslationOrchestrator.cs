using System.Security.Cryptography;
using System.Text;
using AITranslator.Models;

namespace AITranslator.Services;

public sealed class TranslationOrchestrator
{
    private readonly ITranslationProvider _provider;
    private readonly SecretStore _secretStore;
    private readonly SettingsService _settingsService;
    private readonly TranslationCache _cache;
    private readonly PhoneticService _phonetics;

    public TranslationOrchestrator(ITranslationProvider provider, SecretStore secretStore, SettingsService settingsService, TranslationCache cache,
        PhoneticService phonetics)
    {
        _provider = provider;
        _secretStore = secretStore;
        _settingsService = settingsService;
        _cache = cache;
        _phonetics = phonetics;
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("待翻译文本不能为空。", nameof(request));
        }

        var settings = _settingsService.Current.Copy();
        settings.ActiveReasoningEffort = settings.TranslationReasoningEffort;
        var apiKey = await _secretStore.ReadApiKeyAsync(settings.ApiPreset, cancellationToken);
        return await _provider.TranslateAsync(request, settings, apiKey, cancellationToken);
    }

    public async Task<LookupAnalysisResult> LookupAsync(string text, string sourceLanguage, string targetLanguage, string domain,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("查询内容不能为空。", nameof(text));
        }

        var settings = _settingsService.Current.Copy();
        settings.ActiveReasoningEffort = settings.TranslationReasoningEffort;
        sourceLanguage = LanguageCatalog.NormalizeTranslationLanguage(sourceLanguage);
        targetLanguage = LanguageCatalog.NormalizeTranslationLanguage(targetLanguage);
        var cacheKey = CreateLookupCacheKey(text, sourceLanguage, targetLanguage, domain, settings, _provider.ProviderId);
        var cached = await _cache.GetAsync<LookupAnalysisResult>(TranslationCache.AiLookupBucket, cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached with { FromCache = true };
        }

        var apiKey = await _secretStore.ReadApiKeyAsync(settings.ApiPreset, cancellationToken);
        var result = await _provider.LookupAsync(text.Trim(), sourceLanguage, targetLanguage, domain, settings, apiKey, cancellationToken);
        result = _phonetics.CompleteLookup(result);
        await _cache.SetAsync(TranslationCache.AiLookupBucket, cacheKey, result, cancellationToken);
        return result;
    }

    public async Task<CaptureTranslationResult> TranslateCaptureAsync(CaptureTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        var lines = request.Lines.Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0)
        {
            throw new ArgumentException("截屏识别文本不能为空。", nameof(request));
        }

        if (lines.Length > 200 || lines.Sum(line => line.Length) > 30_000)
        {
            throw new ArgumentException("截屏识别文本过多，请缩小截取范围。", nameof(request));
        }

        var normalizedRequest = request with { Lines = lines };
        var settings = _settingsService.Current.Copy();
        settings.ActiveReasoningEffort = settings.TranslationReasoningEffort;
        var apiKey = await _secretStore.ReadApiKeyAsync(settings.ApiPreset, cancellationToken);
        return await _provider.TranslateCaptureAsync(normalizedRequest, settings, apiKey, cancellationToken);
    }

    public async Task<DocumentTranslationResult> TranslateDocumentAsync(DocumentTranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Units.Count == 0 || request.Units.Any(unit => string.IsNullOrWhiteSpace(unit.Text)) ||
            request.Units.Select(unit => unit.Id).Distinct().Count() != request.Units.Count)
        {
            throw new ArgumentException("文件翻译文本结构无效。", nameof(request));
        }

        var settings = _settingsService.Current.Copy();
        settings.ActiveReasoningEffort = settings.FileTranslationReasoningEffort;
        var apiKey = await _secretStore.ReadApiKeyAsync(settings.ApiPreset, cancellationToken);
        return await _provider.TranslateDocumentAsync(request, settings, apiKey, cancellationToken);
    }

    public Task<TranslationResult> TestConnectionAsync(AppSettings settings, string apiKey, CancellationToken cancellationToken = default)
    {
        var requestSettings = settings.Copy();
        requestSettings.ActiveReasoningEffort = requestSettings.TranslationReasoningEffort;
        return _provider.TranslateAsync(new TranslationRequest("Connection test", "en", "zh-CN"), requestSettings, apiKey, cancellationToken);
    }

    private static string CreateLookupCacheKey(string text, string sourceLanguage, string targetLanguage, string domain, AppSettings settings,
        string providerId)
    {
        var canonical = string.Join('\u001F', providerId, settings.ApiPreset, settings.ApiProtocol, settings.TranslationEndpoint.Trim(),
            settings.TranslationModel.Trim(), settings.ActiveReasoningEffort, settings.IndustryContext?.Trim() ?? string.Empty,
            settings.AppLanguage, sourceLanguage, targetLanguage, domain, text.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
