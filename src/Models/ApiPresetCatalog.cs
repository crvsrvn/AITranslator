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
    IReadOnlyList<string> SupportedReasoningEfforts)
{
    public override string ToString() => DisplayName;
}

public sealed record ReasoningEffortOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class ApiPresetCatalog
{
    public static IReadOnlyList<ApiPreset> Presets { get; } =
    [
        new("openai-gpt", "OpenAI GPT", ApiProtocolNames.OpenAiChat, "https://api.openai.com/v1",
            ["gpt-5.6-terra", "gpt-5.6-sol", "gpt-5.6-luna", "gpt-5.6"], "Authorization", "Bearer", "medium",
            [string.Empty, "off", "low", "medium", "high", "xhigh", "max"]),
        new("anthropic-claude", "Anthropic Claude", ApiProtocolNames.AnthropicMessages, "https://api.anthropic.com/v1",
            ["claude-sonnet-5", "claude-opus-5", "claude-fable-5"], "x-api-key", string.Empty, "medium",
            [string.Empty, "off", "low", "medium", "high", "xhigh", "max"]),
        new("deepseek", "DeepSeek", ApiProtocolNames.OpenAiChat, "https://api.deepseek.com",
            ["deepseek-v4-flash", "deepseek-v4-pro"], "Authorization", "Bearer", "high",
            [string.Empty, "off", "high", "max"])
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

    public static IReadOnlyList<ReasoningEffortOption> GetReasoningEfforts(ApiPreset preset, string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !preset.Models.Contains(model.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var supported = preset.SupportedReasoningEfforts;
        if (string.Equals(preset.Id, "anthropic-claude", StringComparison.OrdinalIgnoreCase) &&
            model.Contains("fable", StringComparison.OrdinalIgnoreCase))
        {
            supported = supported.Where(item => !string.Equals(item, "off", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return ReasoningEfforts.Where(item => supported.Contains(item.Value, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
