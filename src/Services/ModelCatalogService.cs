using System.Net;
using System.Text.Json;
using AITranslator.Models;

namespace AITranslator.Services;

public sealed record ModelCatalog(IReadOnlyList<string> Models, IReadOnlyDictionary<string, List<string>> ReasoningEfforts);

/// <summary>通过各服务官方的 GET /models 接口获取当前可用模型；仅 Claude 的响应携带推理强度能力。</summary>
public sealed class ModelCatalogService
{
    private static readonly string[] ChatPathSuffixes = ["/chat/completions", "/messages"];
    private static readonly string[] AnthropicEffortLevels = ["low", "medium", "high", "xhigh", "max"];
    // OpenAI 的 /models 会混入 embedding、语音、图像等模型，接口本身不提供能力字段，只能按名称排除。
    private static readonly string[] OpenAiNonChatMarkers =
        ["audio", "realtime", "transcribe", "tts", "image", "instruct", "search", "embedding", "moderation", "codex"];

    private readonly HttpClient _httpClient;

    public ModelCatalogService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ModelCatalog> FetchAsync(ApiPreset preset, ApiProfileSettings profile, string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranslationServiceException("请先填写 API 密钥。");
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, ResolveModelsEndpoint(profile.TranslationEndpoint, preset.Protocol));
        AddAuthenticationHeader(message, profile, apiKey);
        if (preset.Protocol == ApiProtocolNames.AnthropicMessages)
        {
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationServiceException("获取模型列表请求超时。");
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationServiceException($"无法连接模型列表接口：{exception.Message}", exception);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationServiceException(FormatApiError(response.StatusCode, body));
            }

            return ParseCatalog(preset, body);
        }
    }

    private static ModelCatalog ParseCatalog(ApiPreset preset, string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new TranslationServiceException("模型列表响应格式无法识别。");
        }

        var models = new List<string>();
        var efforts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var entries = data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object);
        if (IsOpenAi(preset))
        {
            // OpenAI 未按发布时间排序，按 created 倒序让最新模型排在前面。
            entries = entries.OrderByDescending(item =>
                item.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number ? created.GetInt64() : 0);
        }

        foreach (var item in entries)
        {
            var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(id) || !IsChatModel(preset, id))
            {
                continue;
            }

            models.Add(id);
            if (TryReadAnthropicEfforts(item, out var levels))
            {
                efforts[id] = levels;
            }
        }

        if (models.Count == 0)
        {
            throw new TranslationServiceException("服务未返回任何可用模型。");
        }

        return new ModelCatalog(models, efforts);
    }

    private static bool IsOpenAi(ApiPreset preset) => string.Equals(preset.Id, "openai-gpt", StringComparison.OrdinalIgnoreCase);

    private static bool IsChatModel(ApiPreset preset, string id)
    {
        if (!IsOpenAi(preset))
        {
            return true;
        }

        var isGptFamily = id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) || id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1]);
        return isGptFamily && !OpenAiNonChatMarkers.Any(marker => id.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadAnthropicEfforts(JsonElement item, out List<string> levels)
    {
        levels = [];
        if (!item.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("effort", out var effort) || effort.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var level in AnthropicEffortLevels)
        {
            if (effort.TryGetProperty(level, out var support) && support.ValueKind == JsonValueKind.Object &&
                support.TryGetProperty("supported", out var supported) && supported.ValueKind == JsonValueKind.True)
            {
                levels.Add(level);
            }
        }

        return true;
    }

    private static Uri ResolveModelsEndpoint(string value, string protocol)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new TranslationServiceException("请填写完整的翻译 API 地址。");
        }

        var isAllowed = endpoint.Scheme == Uri.UriSchemeHttps ||
                        endpoint.Scheme == Uri.UriSchemeHttp && OpenAiCompatibleTranslationProvider.IsPrivateNetworkEndpoint(endpoint);
        if (!isAllowed)
        {
            throw new TranslationServiceException("API 地址必须使用 HTTPS；本机或局域网私有 IP 地址可使用 HTTP。");
        }

        var path = endpoint.AbsolutePath.TrimEnd('/');
        var chatSuffix = ChatPathSuffixes.FirstOrDefault(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (chatSuffix is not null)
        {
            path = path[..^chatSuffix.Length];
        }

        var isAnthropic = protocol == ApiProtocolNames.AnthropicMessages;
        if (path.Length == 0 && isAnthropic)
        {
            path = "/v1";
        }

        // Claude 的列表接口默认分页 20 条，一次取满上限以免遗漏。
        return new UriBuilder(endpoint) { Path = $"{path}/models", Query = isAnthropic ? "limit=1000" : string.Empty }.Uri;
    }

    private static void AddAuthenticationHeader(HttpRequestMessage message, ApiProfileSettings profile, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(profile.ApiKeyHeader))
        {
            return;
        }

        var prefix = profile.ApiKeyPrefix.Trim();
        var value = string.IsNullOrEmpty(prefix) ? apiKey.Trim() : $"{prefix} {apiKey.Trim()}";
        if (!message.Headers.TryAddWithoutValidation(profile.ApiKeyHeader.Trim(), value))
        {
            throw new TranslationServiceException("API 鉴权头名称无效。");
        }
    }

    private static string FormatApiError(HttpStatusCode statusCode, string body)
    {
        var detail = body.Length > 300 ? body[..300] + "…" : body;
        return $"获取模型列表失败（HTTP {(int)statusCode}）：{detail}";
    }
}
