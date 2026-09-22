namespace AITranslator.Models;

public static class ApiProtocolNames
{
    public const string OpenAiChat = "openai-chat";
    public const string AnthropicMessages = "anthropic-messages";
}

public sealed record ApiPreset(
    string Id,
    string DisplayName,
    string Protocol,
    string Endpoint,
    IReadOnlyList<string> Models,
    string ApiKeyHeader,
    string ApiKeyPrefix,
    string DefaultReasoningEffort,
    IReadOnlyList<string> SupportedReasoningEfforts,
    IReadOnlyList<string> AlwaysThinkingModelPrefixes)
{
    public override string ToString() => DisplayName;
}

public sealed record ReasoningEffortOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class ApiPresetCatalog
{
    // 模型与推理强度取自各服务官方文档（2026-09）：
    // OpenAI  https://developers.openai.com/api/docs/models  gpt-6-astra 不支持 reasoning_effort=none
    // Claude  https://platform.claude.com/docs/en/about-claude/models/overview  Fable 系列思考始终开启
    // DeepSeek https://api-docs.deepseek.com/guides/thinking_mode  reasoning_effort 接受 low/high/max
    public static IReadOnlyList<ApiPreset> Presets { get; } =
    [
        new("openai-gpt", "OpenAI GPT", ApiProtocolNames.OpenAiChat, "https://api.openai.com/v1",
            ["gpt-5.6-sol", "gpt-6-astra", "gpt-5.6"], "Authorization", "Bearer", "medium",
            [string.Empty, "off", "low", "medium", "high", "xhigh", "max"], ["gpt-6"]),
        new("anthropic-claude", "Anthropic Claude", ApiProtocolNames.AnthropicMessages, "https://api.anthropic.com/v1",
            ["claude-sonnet-5", "claude-opus-5", "claude-fable-5-1"], "x-api-key", string.Empty, "medium",
            [string.Empty, "off", "low", "medium", "high", "xhigh", "max"], ["claude-fable"]),
        new("deepseek", "DeepSeek", ApiProtocolNames.OpenAiChat, "https://api.deepseek.com",
            ["deepseek-flash", "deepseek-v4-pro"], "Authorization", "Bearer", "high",
            [string.Empty, "off", "low", "high", "max"], [])
    ];

    public static IReadOnlyList<ReasoningEffortOption> ReasoningEfforts { get; } =
    [
        new(string.Empty, "自动（服务默认）"),
        new("off", "关闭（非思考模式）"),
        new("low", "低"),
        new("medium", "中"),
        new("high", "高"),
        new("xhigh", "很高"),
        new("max", "最大")
    ];

    public static ApiPreset Find(string? id) =>
        Presets.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

    /// <summary>优先使用从服务拉取并保存在配置中的模型列表，否则回退到内置模板。</summary>
    public static IReadOnlyList<string> GetModels(ApiPreset preset, ApiProfileSettings? profile) =>
        profile?.Models is { Count: > 0 } ? profile.Models : preset.Models;

    public static IReadOnlyList<ReasoningEffortOption> GetReasoningEfforts(ApiPreset preset, string? model, ApiProfileSettings? profile)
    {
        var trimmedModel = model?.Trim() ?? string.Empty;
        if (trimmedModel.Length == 0 || !GetModels(preset, profile).Contains(trimmedModel, StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        // 服务返回的强度等级（目前仅 Claude 提供）只包含 low..max，自动/关闭由本地补齐。
        var fetchedLevels = profile?.ModelReasoningEfforts?
            .FirstOrDefault(pair => string.Equals(pair.Key, trimmedModel, StringComparison.OrdinalIgnoreCase)).Value;
        IEnumerable<string> supported = fetchedLevels is null
            ? preset.SupportedReasoningEfforts
            : [string.Empty, "off", .. fetchedLevels];
        if (preset.AlwaysThinkingModelPrefixes.Any(prefix => trimmedModel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            supported = supported.Where(item => !string.Equals(item, "off", StringComparison.OrdinalIgnoreCase));
        }

        var supportedSet = supported.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ReasoningEfforts.Where(item => supportedSet.Contains(item.Value)).ToArray();
    }
}
