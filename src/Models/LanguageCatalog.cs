namespace AITranslator.Models;

public static class LanguageCatalog
{
    private static readonly string[] TranslationLanguageCodes =
        ["auto", "zh-CN", "zh-TW", "en", "ja", "ko", "fr", "de", "es", "ru", "pt", "it"];

    public static IReadOnlyList<LanguageOption> SourceLanguages { get; } = CreateTranslationLanguages("zh-CN");

    public static IReadOnlyList<LanguageOption> TargetLanguages { get; } = CreateTranslationLanguages("zh-CN");

    public static IReadOnlyList<LanguageOption> InterfaceLanguages { get; } =
    [
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁體中文"),
        new("en", "English")
    ];

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

    public static IReadOnlyList<LanguageOption> CreateTranslationLanguages(string interfaceLanguage) =>
        TranslationLanguageCodes.Select(code => new LanguageOption(code, GetDisplayName(code, interfaceLanguage))).ToArray();

    public static string NormalizeInterfaceLanguage(string? code)
    {
        if (string.Equals(code, "zh-TW", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-TW";
        }

        return string.Equals(code, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh-CN";
    }

    public static string NormalizeTranslationLanguage(string? code) =>
        TranslationLanguageCodes.FirstOrDefault(item => string.Equals(item, code, StringComparison.OrdinalIgnoreCase)) ?? "auto";

    private static string GetDisplayName(string code, string interfaceLanguage)
    {
        interfaceLanguage = NormalizeInterfaceLanguage(interfaceLanguage);
        return (interfaceLanguage, code) switch
        {
            ("zh-TW", "auto") => "自動選擇",
            ("zh-TW", "zh-CN") => "簡體中文",
            ("zh-TW", "zh-TW") => "繁體中文",
            ("zh-TW", "en") => "英文",
            ("zh-TW", "ja") => "日文",
            ("zh-TW", "ko") => "韓文",
            ("zh-TW", "fr") => "法文",
            ("zh-TW", "de") => "德文",
            ("zh-TW", "es") => "西班牙文",
            ("zh-TW", "ru") => "俄文",
            ("zh-TW", "pt") => "葡萄牙文",
            ("zh-TW", "it") => "義大利文",
            ("en", "auto") => "Auto",
            ("en", "zh-CN") => "Simplified Chinese",
            ("en", "zh-TW") => "Traditional Chinese",
            ("en", "en") => "English",
            ("en", "ja") => "Japanese",
            ("en", "ko") => "Korean",
            ("en", "fr") => "French",
            ("en", "de") => "German",
            ("en", "es") => "Spanish",
            ("en", "ru") => "Russian",
            ("en", "pt") => "Portuguese",
            ("en", "it") => "Italian",
            (_, "auto") => "自动选择",
            (_, "zh-CN") => "简体中文",
            (_, "zh-TW") => "繁体中文",
            (_, "en") => "英语",
            (_, "ja") => "日语",
            (_, "ko") => "韩语",
            (_, "fr") => "法语",
            (_, "de") => "德语",
            (_, "es") => "西班牙语",
            (_, "ru") => "俄语",
            (_, "pt") => "葡萄牙语",
            (_, "it") => "意大利语",
            _ => code
        };
    }

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
