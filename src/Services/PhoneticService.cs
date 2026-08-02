using System.Text.RegularExpressions;
using DotNetG2P.English;
using AITranslator.Models;
using ToolGood.Words;

namespace AITranslator.Services;

public sealed partial class PhoneticService : IDisposable
{
    private readonly EnglishG2PEngine _englishEngine = new();

    public string GetToneMarkedPinyin(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !ContainsChinese(text))
        {
            return string.Empty;
        }

        return ChineseRunRegex().Replace(text, match =>
        {
            var compact = WordsHelper.GetPinyin(match.Value, true);
            return PinyinSyllableBoundaryRegex().Replace(compact, " ").ToLowerInvariant();
        }).Trim();
    }

    public string GetEnglishIpa(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !ContainsEnglish(text))
        {
            return string.Empty;
        }

        try
        {
            return NormalizeIpa(_englishEngine.ToIPA(text.Trim()));
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    public PronunciationOption? CreateEnglishPronunciation(string text, string? ipa = null, string label = "美式 IPA",
        string languageCode = "en-US")
    {
        var normalizedIpa = NormalizeIpa(ipa);
        if (string.IsNullOrWhiteSpace(normalizedIpa))
        {
            normalizedIpa = GetEnglishIpa(text);
        }

        return string.IsNullOrWhiteSpace(normalizedIpa) || string.IsNullOrWhiteSpace(text)
            ? null
            : new PronunciationOption(label, normalizedIpa, text.Trim(), languageCode);
    }

    public LookupAnalysisResult CompleteLookup(LookupAnalysisResult result)
    {
        var sourcePinyin = ContainsChinese(result.SourceText)
            ? GetToneMarkedPinyin(result.SourceText)
            : result.SourcePinyin;
        var englishText = string.IsNullOrWhiteSpace(result.EnglishText) && ContainsEnglish(result.SourceText)
            ? result.SourceText
            : result.EnglishText;
        var pronunciations = CompleteEnglishPronunciations(result.EnglishPronunciations, englishText);
        var contextEnglishText = result.ContextEnglishText?.Trim() ?? string.Empty;

        return result with
        {
            SourcePinyin = NullWhenEmpty(sourcePinyin ?? string.Empty),
            ChineseDefinitionPinyin = null,
            EnglishText = englishText.Trim(),
            EnglishPronunciations = pronunciations,
            GeneralMeaningPinyin = null,
            InternetMeaningPinyin = null,
            ProfessionalMeaningPinyins = [],
            ContextChineseDefinitionPinyin = null,
            ContextEnglishText = NullWhenEmpty(contextEnglishText),
            ContextEnglishPronunciations = []
        };
    }

    public static IEnumerable<PronunciationOption> EnumerateLookupPronunciations(LookupAnalysisResult? result)
    {
        if (result is null)
        {
            yield break;
        }

        if (ContainsChinese(result.SourceText) && !string.IsNullOrWhiteSpace(result.SourcePinyin))
        {
            yield return new PronunciationOption("拼音", result.SourcePinyin.Trim(), result.SourceText.Trim(), "zh-CN");
        }

        foreach (var pronunciation in result.EnglishPronunciations)
        {
            yield return pronunciation;
        }
    }

    private IReadOnlyList<PronunciationOption> CompleteEnglishPronunciations(
        IReadOnlyList<PronunciationOption> source, string englishText, string? labelPrefix = null)
    {
        var pronunciations = source
            .Select(item => CreateEnglishPronunciation(
                string.IsNullOrWhiteSpace(item.SpeakText) ? englishText : item.SpeakText,
                item.Ipa,
                $"{labelPrefix}{(string.IsNullOrWhiteSpace(item.Label) ? "IPA" : item.Label)}",
                string.IsNullOrWhiteSpace(item.LanguageCode) ? "en-US" : item.LanguageCode))
            .Where(item => item is not null)
            .Select(item => item!)
            .ToList();

        if (pronunciations.Count == 0)
        {
            var fallback = CreateEnglishPronunciation(englishText, label: $"{labelPrefix}美式 IPA");
            if (fallback is not null)
            {
                pronunciations.Add(fallback);
            }
        }

        return pronunciations;
    }

    public static string NormalizeIpa(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('/', '[', ']').Trim();

    public static bool ContainsChinese(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9fff');

    public static bool ContainsEnglish(string value) =>
        value.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    public void Dispose() => _englishEngine.Dispose();

    private static string? NullWhenEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex("[\\u3400-\\u9fff]+")]
    private static partial Regex ChineseRunRegex();

    [GeneratedRegex("(?<!^)(?=\\p{Lu})")]
    private static partial Regex PinyinSyllableBoundaryRegex();
}
