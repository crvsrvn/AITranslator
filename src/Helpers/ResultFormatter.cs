using System.Text;
using AITranslator.Models;

namespace AITranslator.Helpers;

public static class ResultFormatter
{
    public static string FormatTranslation(TranslationResult result)
    {
        var generalResult = NormalizeLookupLine(result.Translation);
        if (!HasContextTranslation(result))
        {
            return generalResult;
        }

        var contextualResult = NormalizeContextLookupLine(result.ContextualTranslation!, result.ContextName!);
        return string.IsNullOrWhiteSpace(contextualResult) ||
               string.Equals(generalResult, contextualResult, StringComparison.Ordinal)
            ? generalResult
            : $"{generalResult}{Environment.NewLine}{contextualResult}";
    }

    public static string FormatDictionary(DictionaryEntry? entry)
    {
        if (entry is null)
        {
            return "离线英汉词典仅收录英文词头，或未找到该词条。";
        }

        var builder = new StringBuilder();
        foreach (var group in entry.Definitions.GroupBy(item => item.PartOfSpeech))
        {
            if (!string.IsNullOrWhiteSpace(group.Key))
            {
                builder.AppendLine(group.Key);
            }

            var index = 1;
            foreach (var definition in group)
            {
                builder.Append(index++).Append(". ").AppendLine(definition.Definition);
                if (!string.IsNullOrWhiteSpace(definition.Example))
                {
                    builder.Append("   例：").AppendLine(definition.Example);
                }

                if (definition.Synonyms.Count > 0)
                {
                    builder.Append("   同义词：").AppendLine(string.Join("、", definition.Synonyms));
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatAi(TranslationResult? result)
    {
        if (result is null)
        {
            return "尚无 AI 语义结果。";
        }

        var builder = new StringBuilder();
        if (HasContextTranslation(result))
        {
            builder.AppendLine("【通用翻译】");
        }

        builder.Append(result.Translation.Trim());
        if (!string.IsNullOrWhiteSpace(result.GeneralMeaning))
        {
            builder.AppendLine().AppendLine().Append("通用含义：").Append(result.GeneralMeaning);
        }

        if (!string.IsNullOrWhiteSpace(result.InternetMeaning))
        {
            builder.AppendLine().AppendLine().Append("网络含义：").Append(result.InternetMeaning);
        }

        if (result.ProfessionalMeanings.Count > 0)
        {
            builder.AppendLine().AppendLine().AppendLine("专业含义：");
            foreach (var meaning in result.ProfessionalMeanings)
            {
                builder.Append("• ").AppendLine(meaning);
            }
        }

        AppendContextTranslation(builder, result);

        if (result.FromCache)
        {
            builder.AppendLine().AppendLine().Append("本地缓存");
        }

        return builder.ToString().TrimEnd();
    }

    public static string FormatLookupAi(LookupAnalysisResult? result)
    {
        if (result is null)
        {
            return "尚无 AI 查词结果。";
        }

        var generalResult = NormalizeLookupLine(result.ChineseDefinition);
        if (string.IsNullOrWhiteSpace(result.ContextName))
        {
            return generalResult;
        }

        var contextualSource = result.ContextChineseDefinition;
        if (string.IsNullOrWhiteSpace(contextualSource) && result.ProfessionalMeanings.Count > 0)
        {
            contextualSource = result.ProfessionalMeanings[0];
        }

        var contextualResult = NormalizeContextLookupLine(contextualSource ?? string.Empty, result.ContextName);
        return string.IsNullOrWhiteSpace(contextualResult) ||
               string.Equals(generalResult, contextualResult, StringComparison.Ordinal)
            ? generalResult
            : $"{generalResult}{Environment.NewLine}{contextualResult}";
    }

    private static bool HasContextTranslation(TranslationResult result) =>
        !string.IsNullOrWhiteSpace(result.ContextName) && !string.IsNullOrWhiteSpace(result.ContextualTranslation);

    private static void AppendContextTranslation(StringBuilder builder, TranslationResult result)
    {
        if (!HasContextTranslation(result))
        {
            return;
        }

        builder.AppendLine().AppendLine()
            .Append('【').Append(result.ContextName!.Trim()).AppendLine("：行业/语境翻译】")
            .Append(result.ContextualTranslation!.Trim());
    }

    private static string NormalizeLookupLine(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimStart('•', '·', '-', ' ');

    private static string NormalizeContextLookupLine(string value, string contextName)
    {
        var normalized = NormalizeLookupLine(value);
        var context = NormalizeLookupLine(contextName);
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(context))
        {
            return normalized;
        }

        var prefixes = new[]
        {
            $"在{context}领域中",
            $"在{context}领域内",
            $"在{context}领域",
            $"在{context}中"
        };
        foreach (var prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized[prefix.Length..].TrimStart('，', ',', '：', ':', '；', ';', ' ');
            }
        }

        if (normalized.StartsWith(context, StringComparison.OrdinalIgnoreCase) &&
            normalized.Length > context.Length &&
            "，,：:".Contains(normalized[context.Length]))
        {
            return normalized[(context.Length + 1)..].TrimStart();
        }

        return normalized;
    }
}
