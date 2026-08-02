using AITranslator.Models;
using Microsoft.Data.Sqlite;

namespace AITranslator.Services;

public sealed class OpenDictionaryService
{
    private static readonly (string Prefix, string Label)[] PartOfSpeechPrefixes =
    [
        ("interj.", "感叹词"),
        ("abbr.", "缩写"),
        ("prep.", "介词"),
        ("pron.", "代词"),
        ("conj.", "连词"),
        ("adv.", "副词"),
        ("adj.", "形容词"),
        ("aux.", "助动词"),
        ("num.", "数词"),
        ("art.", "冠词"),
        ("vt.", "及物动词"),
        ("vi.", "不及物动词"),
        ("ad.", "副词"),
        ("a.", "形容词"),
        ("v.", "动词"),
        ("n.", "名词")
    ];

    private static readonly IReadOnlyDictionary<string, string> DomainLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["计"] = "计算机",
        ["医"] = "医学",
        ["化"] = "化学",
        ["法"] = "法律",
        ["经"] = "经济",
        ["网络"] = "网络"
    };

    private readonly string _connectionString;
    private readonly PhoneticService _phonetics;

    public OpenDictionaryService(AppPaths paths, PhoneticService phonetics)
    {
        if (!File.Exists(paths.DictionaryDatabase))
        {
            throw new FileNotFoundException("未找到随程序提供的离线英汉词典。", paths.DictionaryDatabase);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DictionaryDatabase,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        _phonetics = phonetics;
    }

    public async Task<DictionaryEntry?> LookupEnglishAsync(string word, CancellationToken cancellationToken = default)
    {
        var normalizedWord = word.Trim().ToLowerInvariant();
        if (normalizedWord.Length is 0 or > 200 || normalizedWord.Any(character => !char.IsAscii(character) || char.IsControl(character)))
        {
            return null;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT word, phonetic, translation FROM entries WHERE word = $word COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("$word", normalizedWord);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var definitions = ParseDefinitions(reader.GetString(2));
        if (definitions.Count == 0)
        {
            return null;
        }

        var entryWord = reader.GetString(0);
        var pronunciation = _phonetics.CreateEnglishPronunciation(entryWord);
        return new DictionaryEntry(entryWord, reader.IsDBNull(1) ? null : reader.GetString(1), null, definitions,
            pronunciation is null ? [] : [pronunciation]);
    }

    private IReadOnlyList<DictionaryDefinition> ParseDefinitions(string translation)
    {
        var definitions = new List<DictionaryDefinition>();
        var normalizedTranslation = NormalizeLineBreaks(translation);
        foreach (var sourceLine in normalizedTranslation.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = sourceLine;
            var label = "中文释义";

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                var tagEnd = line.IndexOf(']');
                if (tagEnd > 1)
                {
                    var domain = line[1..tagEnd];
                    label = DomainLabels.TryGetValue(domain, out var domainLabel) ? domainLabel : ContainsChinese(domain) ? domain : "专业释义";
                    line = line[(tagEnd + 1)..].TrimStart();
                }
            }

            foreach (var (prefix, partOfSpeech) in PartOfSpeechPrefixes)
            {
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                label = partOfSpeech;
                line = line[prefix.Length..].TrimStart();
                break;
            }

            if (ContainsChinese(line))
            {
                definitions.Add(new DictionaryDefinition(label, line, null, []));
            }
        }

        return definitions;
    }

    private static string NormalizeLineBreaks(string value) =>
        value.Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\n", StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static bool ContainsChinese(string value) =>
        value.Any(character => character is >= '\u3400' and <= '\u9fff');
}
