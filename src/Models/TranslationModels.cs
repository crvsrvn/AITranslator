namespace AITranslator.Models;

public sealed record TranslationRequest(
    string Text,
    string SourceLanguage,
    string TargetLanguage,
    string Domain = "general",
    bool IncludeSemanticAnalysis = false);

public sealed record TranslationResult(
    string Translation,
    string? GeneralMeaning,
    string? InternetMeaning,
    IReadOnlyList<string> ProfessionalMeanings,
    string Provider,
    bool FromCache = false)
{
    public string? ContextName { get; init; }

    public string? ContextualTranslation { get; init; }
}

public sealed record DocumentTextUnit(int Id, string Text);

public sealed record DocumentTranslationRequest(
    IReadOnlyList<DocumentTextUnit> Units,
    string SourceLanguage,
    string TargetLanguage,
    string Domain = "general",
    string GlobalReferenceText = "");

public sealed record DocumentTranslationResult(IReadOnlyList<DocumentTextUnit> Units, string Provider, bool FromCache = false);

public sealed record PronunciationOption(string Label, string Ipa, string SpeakText, string LanguageCode = "en-US");

public sealed record LookupAnalysisResult(
    string DetectedLanguage,
    string TargetLanguage,
    string SourceText,
    string? SourcePinyin,
    string Definition,
    string? DefinitionPinyin,
    string EnglishText,
    IReadOnlyList<PronunciationOption> EnglishPronunciations,
    string? GeneralMeaning,
    string? GeneralMeaningPinyin,
    string? InternetMeaning,
    string? InternetMeaningPinyin,
    IReadOnlyList<string> ProfessionalMeanings,
    IReadOnlyList<string> ProfessionalMeaningPinyins,
    string Provider,
    bool FromCache = false)
{
    public string? ContextName { get; init; }

    public string? ContextDefinition { get; init; }

    public string? ContextExplanationZh { get; init; }

    public string? ContextDefinitionPinyin { get; init; }

    public string? ContextEnglishText { get; init; }

    public IReadOnlyList<PronunciationOption> ContextEnglishPronunciations { get; init; } = [];
}

public sealed record DictionaryDefinition(
    string PartOfSpeech,
    string Definition,
    string? Example,
    IReadOnlyList<string> Synonyms,
    string? Pinyin = null);

public sealed record DictionaryEntry(
    string Word,
    string? Phonetic,
    string? AudioUrl,
    IReadOnlyList<DictionaryDefinition> Definitions,
    IReadOnlyList<PronunciationOption> Pronunciations,
    bool FromCache = false);

public sealed record LookupResult(DictionaryEntry? Dictionary, LookupAnalysisResult? AiResult);

public sealed record FileTranslationProgress(int Completed, int Total, string CurrentItem)
{
    public double Percentage => Total == 0 ? 0 : (double)Completed / Total * 100;
}

public sealed record FileTranslationReport(string SourcePath, string OutputPath, int TranslatedUnitCount);
