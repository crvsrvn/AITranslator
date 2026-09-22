using System.Text.Json.Serialization;

namespace AITranslator.Models;

public sealed class AppSettings
{
    public string ApiPreset { get; set; } = "openai-gpt";

    public string ApiProtocol { get; set; } = ApiProtocolNames.OpenAiChat;

    public string TranslationEndpoint { get; set; } = "https://api.openai.com/v1";

    public string TranslationModel { get; set; } = "gpt-5.6-sol";

    public string TranslationReasoningEffort { get; set; } = "medium";

    public string FileTranslationReasoningEffort { get; set; } = "medium";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }

    [JsonIgnore]
    public string ActiveReasoningEffort { get; set; } = string.Empty;

    public string ApiKeyHeader { get; set; } = "Authorization";

    public string ApiKeyPrefix { get; set; } = "Bearer";

    public string IndustryContext { get; set; } = string.Empty;

    public string AppLanguage { get; set; } = "zh-CN";

    public string TextSourceLanguage { get; set; } = "auto";

    public string TextTargetLanguage { get; set; } = "auto";

    public Dictionary<string, ApiProfileSettings> ApiProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Theme { get; set; } = "System";

    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    public double FontSize { get; set; } = 15;

    public string ToggleWindowShortcut { get; set; } = "Ctrl+Alt+Space";

    public string SelectionShortcut { get; set; } = "Ctrl+Alt+D";

    public string SpeakShortcut { get; set; } = "Ctrl+Alt+S";

    public string CaptureShortcut { get; set; } = "Ctrl+Shift+A";

    public AppSettings Copy()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ApiProfiles = ApiProfiles is null
            ? new Dictionary<string, ApiProfileSettings>(StringComparer.OrdinalIgnoreCase)
            : ApiProfiles
                .Where(item => item.Value is not null)
                .ToDictionary(item => item.Key, item => item.Value.Copy(), StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}

public sealed class ApiProfileSettings
{
    public string TranslationEndpoint { get; set; } = string.Empty;

    public string TranslationModel { get; set; } = string.Empty;

    public string ApiKeyHeader { get; set; } = string.Empty;

    public string ApiKeyPrefix { get; set; } = string.Empty;

    /// <summary>从服务 /models 接口拉取的模型列表；为空时使用内置模板。</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>服务返回的各模型支持的推理强度（目前仅 Claude 提供）。</summary>
    public Dictionary<string, List<string>> ModelReasoningEfforts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public ApiProfileSettings Copy()
    {
        var copy = (ApiProfileSettings)MemberwiseClone();
        copy.Models = [.. Models ?? []];
        copy.ModelReasoningEfforts = ModelReasoningEfforts?
            .ToDictionary(item => item.Key, item => new List<string>(item.Value ?? []), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}
