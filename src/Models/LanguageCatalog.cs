namespace AITranslator.Models;

public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageOption> SourceLanguages { get; } =
    [
        new("auto", "自动检测"),
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁体中文"),
        new("en", "英语"),
        new("ja", "日语"),
        new("ko", "韩语"),
        new("fr", "法语"),
        new("de", "德语"),
        new("es", "西班牙语"),
        new("ru", "俄语"),
        new("pt", "葡萄牙语"),
        new("it", "意大利语")
    ];

    public static IReadOnlyList<LanguageOption> TargetLanguages { get; } = SourceLanguages.Where(item => item.Code != "auto").ToArray();

    public static IReadOnlyList<string> Domains { get; } =
    [
        "通用", "互联网", "计算机", "医学", "法律", "金融", "工程", "学术"
    ];

    public static string ToPromptName(string code) => code switch
    {
        "auto" => "Auto-detect",
        "zh-CN" => "Simplified Chinese",
        "zh-TW" => "Traditional Chinese",
        "en" => "English",
        "ja" => "Japanese",
        "ko" => "Korean",
        "fr" => "French",
        "de" => "German",
        "es" => "Spanish",
        "ru" => "Russian",
        "pt" => "Portuguese",
        "it" => "Italian",
        _ => code
    };

    public static string ToDomainCode(string displayName) => displayName switch
    {
        "互联网" => "internet",
        "计算机" => "computing",
        "医学" => "medicine",
        "法律" => "law",
        "金融" => "finance",
        "工程" => "engineering",
        "学术" => "academic",
        _ => "general"
    };
}