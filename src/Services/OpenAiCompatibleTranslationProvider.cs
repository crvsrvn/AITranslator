using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AITranslator.Models;

namespace AITranslator.Services;

public sealed class OpenAiCompatibleTranslationProvider : ITranslationProvider
{
    private const string TranslationSystemPrompt = """
                                                    You are a precise professional translator. Preserve names, numbers, formatting, code, and terminology.
                                                    The source and target language requirements in the user message are authoritative. Never choose a different
                                                    output language. When the effective source and target languages are the same, explain the source meaning in
                                                    that language in a concise dictionary style instead of copying or paraphrasing it without explanation.
                                                    Return JSON only, using this schema:
                                                   {
                                                     "translation": "complete translated text",
                                                     "contextual_translation": "translation adapted to the configured industry/context, or empty string",
                                                     "general_meaning": "brief general meaning or empty string",
                                                     "internet_meaning": "online, slang, or community meaning or empty string",
                                                     "professional_meanings": ["domain-specific meaning with a short note"]
                                                   }
                                                   Keep translation general and context-neutral. If an industry/context is configured, also return a complete
                                                   specialized translation in contextual_translation; otherwise keep it empty. Never replace the general translation
                                                   with the specialized translation. Never add Markdown fences. When semantic analysis is not requested, keep the
                                                    three meaning fields empty. contextual_translation must contain only the final specialized translation.
                                                    Never add a heading, field label, context name, or introductory phrase such as "In this context" or "在该领域中".
                                                   """;

    private const string LookupSystemPrompt = """
                                               You are a precise multilingual lexicographer. Analyze the query as a word or short phrase and obey the source
                                               and target language requirements in the user message. Never silently replace the requested target language.
                                               When the effective source and target languages are the same, write a concise dictionary-style explanation in
                                               that language instead of copying the query.
                                               Return JSON only, using this schema:
                                               {
                                                 "detected_language": "detected BCP-47 language code",
                                                 "target_language": "actual output BCP-47 language code",
                                                 "source_text": "the exact query",
                                                 "source_pinyin": "tone-marked Hanyu Pinyin for a Chinese query, otherwise empty",
                                                 "definition": "one concise target-language translation or same-language explanation",
                                                 "english_text": "English headword or translation when source or target is English, otherwise empty",
                                                 "english_pronunciations": [
                                                   {"label":"US IPA", "ipa":"IPA without slash brackets", "speak_text":"matching English text", "language_code":"en-US"}
                                                 ],
                                                 "contextual_definition": "one concise target-language definition for the configured industry/context, or empty string"
                                               }
                                               Keep definition general, concise, and free of labels, headings, bullets, and line breaks. If an industry/context
                                               is configured, contextual_definition must be one concise domain-specific sentence in the same target language.
                                               Begin it directly with the result content; never begin with the context name, a heading, or a field label.
                                               Otherwise keep contextual_definition empty.
                                               For a Chinese query, source_pinyin must contain tone-marked Hanyu Pinyin. Do not add pinyin to Chinese
                                               definitions or explanations. Every English headword or general English translation must have IPA.
                                               Never add Markdown fences.
                                              """;

    private const string CaptureTranslationSystemPrompt = """
                                                          You translate OCR text lines while preserving their one-to-one layout mapping.
                                                          Return JSON only, using this schema:
                                                          {
                                                            "translations": [
                                                              {"index": 0, "translation": "general translation", "contextual_translation": "context-adapted translation or empty string"}
                                                            ]
                                                          }
                                                          Return exactly one item for every input index and keep the original order. Do not merge, split, omit, or
                                                          renumber lines. Every natural-language translation must be written in the requested target language;
                                                          never copy the source text as its translation when source and target languages differ. Preserve names,
                                                          numbers, code, and terminology. If an industry/context is configured,
                                                          contextual_translation must contain only the final context-adapted translation without a heading or
                                                          introductory phrase; otherwise keep it empty. Never add Markdown fences.
                                                          """;

    private const string DocumentTranslationSystemPrompt = """
                                                           You translate atomic writeback units from one complete document in a single response. You receive
                                                           exactly two inputs. Input 1 is the complete source document for global context only: never translate,
                                                           repeat, summarize, or output Input 1. Input 2 is the only content to translate. Use Input 1 and the
                                                           configured industry/context to understand Input 2, then return exactly one final translation for every
                                                           Input 2 unit. Never separate general and contextual alternatives.
                                                           Return JSON only, using this schema:
                                                           {
                                                             "translations": [
                                                               {"id": 0, "text": "final translated text"}
                                                             ]
                                                           }
                                                           Return every Input 2 id exactly once. Copy each id unchanged. Do not merge, split, omit, or renumber
                                                           units. Every Input 2 unit is one atomic visual line, so text must not contain a line break. Preserve
                                                           names, numbers, placeholders, code, URLs, and terminology. Keep each translation's visual length
                                                           close to its source while remaining natural in the requested target language. Do not add headings,
                                                           labels, explanations, alternatives, Markdown, or introductory phrases. Output translations for Input 2 only.
                                                           """;

    private readonly HttpClient _httpClient;

    public OpenAiCompatibleTranslationProvider(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string ProviderId => "configurable-ai";

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, AppSettings settings, string apiKey,
        CancellationToken cancellationToken = default)
    {
        var context = settings.IndustryContext?.Trim() ?? string.Empty;
        var content = await SendPromptAsync(TranslationSystemPrompt, BuildTranslationPrompt(request, context, settings.AppLanguage), settings, apiKey,
            cancellationToken);
        return ParseTranslationContent(content, settings.ApiPreset, context);
    }

    public async Task<CaptureTranslationResult> TranslateCaptureAsync(CaptureTranslationRequest request, AppSettings settings, string apiKey,
        CancellationToken cancellationToken = default)
    {
        var context = settings.IndustryContext?.Trim() ?? string.Empty;
        var prompt = BuildCaptureTranslationPrompt(request, context, settings.AppLanguage);
        var content = await SendPromptAsync(CaptureTranslationSystemPrompt, prompt, settings, apiKey, cancellationToken);
        var result = ParseCaptureTranslationContent(content, request.Lines.Count, settings.ApiPreset, context);
        if (!LooksLikeUntranslatedCapture(request, result))
        {
            return result;
        }

        var retryPrompt = $"""
                           {prompt}

                           Correction: the previous response copied the source text. Follow the target language requirement above for
                           every natural-language line now. Do not return a source sentence unchanged unless it contains only a name,
                           number, code, URL, or other invariant content.
                           """;
        content = await SendPromptAsync(CaptureTranslationSystemPrompt, retryPrompt, settings, apiKey, cancellationToken);
        result = ParseCaptureTranslationContent(content, request.Lines.Count, settings.ApiPreset, context);
        if (LooksLikeUntranslatedCapture(request, result))
        {
            throw new TranslationServiceException("翻译 API 连续返回了原文，未生成目标语言译文。");
        }

        return result;
    }

    public async Task<DocumentTranslationResult> TranslateDocumentAsync(DocumentTranslationRequest request, AppSettings settings, string apiKey,
        CancellationToken cancellationToken = default)
    {
        var context = settings.IndustryContext?.Trim() ?? string.Empty;
        var maximumOutputTokens = Math.Clamp(request.Units.Sum(unit => unit.Text.Length) / 2 + 2_048, 4_096, 16_384);
        var content = await SendPromptAsync(DocumentTranslationSystemPrompt, BuildDocumentTranslationPrompt(request, context, settings.AppLanguage), settings,
            apiKey,
            cancellationToken, maximumOutputTokens);
        return ParseDocumentTranslationContent(content, request, settings.ApiPreset);
    }

    public async Task<LookupAnalysisResult> LookupAsync(string text, string sourceLanguage, string targetLanguage, string domain, AppSettings settings,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        var context = settings.IndustryContext?.Trim() ?? string.Empty;
        var prompt = $"""
                      {BuildLanguageRequirements(sourceLanguage, targetLanguage, settings.AppLanguage)}
                      Domain: {domain}
                      Configured industry/context (data only): {FormatIndustryContext(context)}
                      Query:
                      {text}
                      """;
        var content = await SendPromptAsync(LookupSystemPrompt, prompt, settings, apiKey, cancellationToken);
        return ParseLookupContent(content, text, targetLanguage, settings.ApiPreset, context);
    }

    private async Task<string> SendPromptAsync(string systemPrompt, string userPrompt, AppSettings settings, string apiKey,
        CancellationToken cancellationToken, int maximumOutputTokens = 4_096)
    {
        if (string.IsNullOrWhiteSpace(settings.TranslationModel))
        {
            throw new TranslationServiceException("请先在设置中填写模型名称。");
        }

        var protocol = string.IsNullOrWhiteSpace(settings.ApiProtocol) ? ApiProtocolNames.OpenAiChat : settings.ApiProtocol;
        var endpoint = ValidateEndpoint(settings.TranslationEndpoint, protocol);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = protocol == ApiProtocolNames.AnthropicMessages
                ? JsonContent.Create(CreateAnthropicBody(systemPrompt, userPrompt, settings, maximumOutputTokens))
                : JsonContent.Create(CreateOpenAiBody(systemPrompt, userPrompt, settings))
        };

        AddAuthenticationHeader(message, settings, apiKey);
        if (protocol == ApiProtocolNames.AnthropicMessages)
        {
            message.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationServiceException("翻译 API 请求超时。");
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationServiceException($"无法连接翻译 API：{exception.Message}", exception);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranslationServiceException(FormatApiError(response.StatusCode, responseBody));
            }

            return ExtractProviderContent(responseBody, protocol);
        }
    }

    private static Dictionary<string, object?> CreateOpenAiBody(string systemPrompt, string userPrompt, AppSettings settings)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.TranslationModel.Trim(),
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        var effort = settings.ActiveReasoningEffort.Trim().ToLowerInvariant();
        if (string.Equals(settings.ApiPreset, "deepseek", StringComparison.OrdinalIgnoreCase))
        {
            if (effort == "off")
            {
                body["thinking"] = new { type = "disabled" };
            }
            else if (!string.IsNullOrEmpty(effort))
            {
                var deepSeekEffort = effort is "xhigh" or "max" ? "max" : "high";
                body["thinking"] = new { type = "enabled" };
                body["reasoning_effort"] = deepSeekEffort;
            }
        }
        else if (effort == "off")
        {
            body["reasoning_effort"] = "none";
        }
        else if (!string.IsNullOrEmpty(effort))
        {
            body["reasoning_effort"] = effort;
        }

        return body;
    }

    private static Dictionary<string, object?> CreateAnthropicBody(string systemPrompt, string userPrompt, AppSettings settings,
        int maximumOutputTokens)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = settings.TranslationModel.Trim(),
            ["max_tokens"] = maximumOutputTokens,
            ["system"] = systemPrompt,
            ["messages"] = new object[] { new { role = "user", content = userPrompt } }
        };

        var effort = settings.ActiveReasoningEffort.Trim().ToLowerInvariant();
        if (effort == "off")
        {
            body["thinking"] = new { type = "disabled" };
        }
        else if (!string.IsNullOrEmpty(effort))
        {
            body["output_config"] = new { effort };
        }

        return body;
    }

    private static Uri ValidateEndpoint(string value, string protocol)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var endpoint))
        {
            throw new TranslationServiceException("请填写完整的翻译 API 地址。");
        }

        var isAllowed = endpoint.Scheme == Uri.UriSchemeHttps || endpoint.Scheme == Uri.UriSchemeHttp && IsPrivateNetworkEndpoint(endpoint);
        if (!isAllowed)
        {
            throw new TranslationServiceException("API 地址必须使用 HTTPS；本机或局域网私有 IP 地址可使用 HTTP。");
        }

        return protocol == ApiProtocolNames.AnthropicMessages ? ResolveAnthropicMessagesEndpoint(endpoint) : ResolveChatCompletionsEndpoint(endpoint);
    }

    private static bool IsPrivateNetworkEndpoint(Uri endpoint)
    {
        if (endpoint.IsLoopback)
        {
            return true;
        }

        if (!IPAddress.TryParse(endpoint.DnsSafeHost, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 169 && bytes[1] == 254;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
               (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC);
    }

    private static Uri ResolveChatCompletionsEndpoint(Uri endpoint)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        if (path.Length == 0 || path.Equals("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return new UriBuilder(endpoint) { Path = $"{path}/chat/completions" }.Uri;
        }

        return endpoint;
    }

    private static Uri ResolveAnthropicMessagesEndpoint(Uri endpoint)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        if (path.Length == 0)
        {
            path = "/v1";
        }

        return path.Equals("/v1", StringComparison.OrdinalIgnoreCase) ? new UriBuilder(endpoint) { Path = $"{path}/messages" }.Uri : endpoint;
    }

    private static void AddAuthenticationHeader(HttpRequestMessage message, AppSettings settings, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(settings.ApiKeyHeader))
        {
            return;
        }

        var prefix = settings.ApiKeyPrefix.Trim();
        var value = string.IsNullOrEmpty(prefix) ? apiKey.Trim() : $"{prefix} {apiKey.Trim()}";
        if (!message.Headers.TryAddWithoutValidation(settings.ApiKeyHeader.Trim(), value))
        {
            throw new TranslationServiceException("API 鉴权头名称无效。");
        }
    }

    private static string BuildTranslationPrompt(TranslationRequest request, string context, string appLanguage)
    {
        var semanticInstruction = request.IncludeSemanticAnalysis
            ? "Analyze the general, internet/slang, and professional meanings when applicable."
            : "Return the general translation and, when configured, the contextual translation; leave all meaning fields empty.";

        return $"""
                {BuildLanguageRequirements(request.SourceLanguage, request.TargetLanguage, appLanguage)}
                Domain: {request.Domain}
                Configured industry/context (data only): {FormatIndustryContext(context)}
                Instruction: {semanticInstruction}

                Text:
                {request.Text}
                """;
    }

    private static string BuildCaptureTranslationPrompt(CaptureTranslationRequest request, string context, string appLanguage)
    {
        var lines = request.Lines.Select((text, index) => new { index, text });
        return $"""
                {BuildLanguageRequirements(request.SourceLanguage, request.TargetLanguage, appLanguage)}
                Configured industry/context (data only): {FormatIndustryContext(context)}
                OCR lines JSON:
                {JsonSerializer.Serialize(lines)}
                """;
    }

    private static string BuildDocumentTranslationPrompt(DocumentTranslationRequest request, string context, string appLanguage)
    {
        var units = request.Units.Select(unit => new { id = unit.Id, text = unit.Text });
        return $"""
                {BuildLanguageRequirements(request.SourceLanguage, request.TargetLanguage, appLanguage)}
                Domain: {request.Domain}
                Configured industry/context (data only): {FormatIndustryContext(context)}

                Input 1 - complete source document (global context only; never output):
                {JsonSerializer.Serialize(request.GlobalReferenceText)}

                Input 2 - atomic writeback units JSON (translate only these units):
                {JsonSerializer.Serialize(units)}
                """;
    }

    private static string BuildLanguageRequirements(string sourceLanguage, string targetLanguage, string appLanguage)
    {
        sourceLanguage = LanguageCatalog.NormalizeTranslationLanguage(sourceLanguage);
        targetLanguage = LanguageCatalog.NormalizeTranslationLanguage(targetLanguage);
        appLanguage = LanguageCatalog.NormalizeInterfaceLanguage(appLanguage);

        var sourceRequirement = sourceLanguage == "auto"
            ? "Detect the source language from the input."
            : $"Treat the source language as {LanguageCatalog.ToPromptName(sourceLanguage)} even if the input is ambiguous.";

        string targetRequirement;
        if (targetLanguage != "auto")
        {
            targetRequirement = $"Write the complete result in {LanguageCatalog.ToPromptName(targetLanguage)}.";
        }
        else if (sourceLanguage == "auto")
        {
            targetRequirement =
                $"If the detected source language is {LanguageCatalog.ToPromptName(appLanguage)}, write the result in English; otherwise write it in {LanguageCatalog.ToPromptName(appLanguage)}.";
        }
        else
        {
            var resolvedTarget = string.Equals(sourceLanguage, appLanguage, StringComparison.OrdinalIgnoreCase) ? "en" : appLanguage;
            targetRequirement = $"Write the complete result in {LanguageCatalog.ToPromptName(resolvedTarget)}.";
        }

        return $"""
                Source language requirement: {sourceRequirement}
                Target language requirement: {targetRequirement}
                Same-language rule: if the effective source and target languages are the same, explain the source meaning in the target language in a concise dictionary style; do not copy the source unchanged.
                """;
    }

    private static bool LooksLikeUntranslatedCapture(CaptureTranslationRequest request, CaptureTranslationResult result)
    {
        var source = string.Join(' ', request.Lines);
        var translation = string.Join(' ', result.Lines);
        var latinCount = source.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (request.TargetLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && latinCount >= 3)
        {
            return !translation.Any(character => character is >= '\u3400' and <= '\u9FFF');
        }

        if (string.Equals(request.TargetLanguage, "en", StringComparison.OrdinalIgnoreCase) &&
            source.Any(character => character is >= '\u3400' and <= '\u9FFF'))
        {
            return !translation.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        }

        return request.Lines.Count == result.Lines.Count && request.Lines.Zip(result.Lines)
            .All(pair => string.Equals(pair.First.Trim(), pair.Second.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatIndustryContext(string context) =>
        string.IsNullOrWhiteSpace(context) ? "(none)" : JsonSerializer.Serialize(context.Trim());

    private static string ExtractProviderContent(string responseBody, string protocol)
    {
        try
        {
            using var responseJson = JsonDocument.Parse(responseBody);
            var root = responseJson.RootElement;
            if (protocol == ApiProtocolNames.AnthropicMessages)
            {
                if (!root.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                {
                    throw new TranslationServiceException("Claude API 响应中缺少 content。");
                }

                return string.Join(string.Empty,
                    blocks.EnumerateArray()
                        .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "text" && item.TryGetProperty("text", out _))
                        .Select(item => item.GetProperty("text").GetString()));
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                throw new TranslationServiceException("翻译 API 响应中缺少 choices。");
            }

            var content = choices[0].GetProperty("message").GetProperty("content");
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                return string.Join(string.Empty,
                    content.EnumerateArray().Where(item => item.TryGetProperty("text", out _)).Select(item => item.GetProperty("text").GetString()));
            }

            throw new TranslationServiceException("翻译 API 响应中的 content 类型无效。");
        }
        catch (TranslationServiceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new TranslationServiceException("翻译 API 返回了无法识别的数据格式。", exception);
        }
    }

    private static TranslationResult ParseTranslationContent(string content, string provider, string context)
    {
        var trimmed = StripMarkdownFence(content.Trim());
        try
        {
            using var resultJson = JsonDocument.Parse(trimmed);
            var root = resultJson.RootElement;
            var translation = ReadString(root, "translation");
            if (string.IsNullOrWhiteSpace(translation))
            {
                throw new TranslationServiceException("翻译 API 返回了空译文。");
            }

            return new TranslationResult(translation, NullWhenEmpty(ReadString(root, "general_meaning")),
                NullWhenEmpty(ReadString(root, "internet_meaning")), ReadStringArray(root, "professional_meanings"), provider)
            {
                ContextName = NullWhenEmpty(context),
                ContextualTranslation = NullWhenEmpty(ReadString(root, "contextual_translation"))
            };
        }
        catch (JsonException)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new TranslationServiceException("翻译 API 返回了空内容。");
            }

            return new TranslationResult(trimmed, null, null, [], provider)
            {
                ContextName = NullWhenEmpty(context)
            };
        }
    }

    private static CaptureTranslationResult ParseCaptureTranslationContent(string content, int expectedCount, string provider, string context)
    {
        var trimmed = StripMarkdownFence(content.Trim());
        try
        {
            using var resultJson = JsonDocument.Parse(trimmed);
            if (!resultJson.RootElement.TryGetProperty("translations", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                throw new TranslationServiceException("翻译 API 的截屏结果中缺少 translations。");
            }

            var translations = new string?[expectedCount];
            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var indexValue) ||
                    !indexValue.TryGetInt32(out var index) || index < 0 || index >= expectedCount)
                {
                    continue;
                }

                var general = ReadString(item, "translation");
                var contextual = ReadString(item, "contextual_translation");
                translations[index] = !string.IsNullOrWhiteSpace(context) && !string.IsNullOrWhiteSpace(contextual)
                    ? contextual.Trim()
                    : NullWhenEmpty(general) ?? NullWhenEmpty(contextual);
            }

            if (translations.Any(string.IsNullOrWhiteSpace))
            {
                throw new TranslationServiceException("翻译 API 未返回完整的逐行截屏译文。");
            }

            return new CaptureTranslationResult(translations.Select(value => value!).ToArray(), provider);
        }
        catch (TranslationServiceException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new TranslationServiceException("翻译 API 返回了无法识别的逐行截屏译文。", exception);
        }
    }

    private static DocumentTranslationResult ParseDocumentTranslationContent(string content, DocumentTranslationRequest request, string provider)
    {
        var trimmed = StripMarkdownFence(content.Trim());
        try
        {
            using var resultJson = JsonDocument.Parse(trimmed);
            var root = resultJson.RootElement;
            JsonElement values;
            if (root.ValueKind == JsonValueKind.Array)
            {
                values = root;
            }
            else if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("translations", out values) ||
                     values.ValueKind != JsonValueKind.Array)
            {
                throw new TranslationServiceException("翻译 API 返回的文件文本结构不完整。");
            }

            var sourceById = request.Units.ToDictionary(unit => unit.Id);
            var translatedById = new Dictionary<int, string>();
            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idValue) || !idValue.TryGetInt32(out var id) ||
                    !sourceById.ContainsKey(id) || translatedById.ContainsKey(id))
                {
                    throw new TranslationServiceException("翻译 API 改变了文件文本单元的标识。");
                }

                var text = DocumentTextLayout.NormalizeSingleLine(ReadString(item, "text"));
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new TranslationServiceException("翻译 API 返回了空的文件译文单元。");
                }

                translatedById.Add(id, text);
            }

            if (translatedById.Count != sourceById.Count)
            {
                throw new TranslationServiceException("翻译 API 返回的文件文本结构不完整。");
            }

            var translatedUnits = request.Units.Select(unit => new DocumentTextUnit(unit.Id, translatedById[unit.Id])).ToArray();
            return new DocumentTranslationResult(translatedUnits, provider);
        }
        catch (TranslationServiceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new TranslationServiceException("翻译 API 返回了无法识别的文件译文结构。", exception);
        }
    }

    private static LookupAnalysisResult ParseLookupContent(string content, string sourceText, string requestedTargetLanguage, string provider, string context)
    {
        var trimmed = StripMarkdownFence(content.Trim());
        try
        {
            using var resultJson = JsonDocument.Parse(trimmed);
            var root = resultJson.RootElement;
            var definition = ReadString(root, "definition");
            if (string.IsNullOrWhiteSpace(definition))
            {
                throw new TranslationServiceException("翻译 API 返回了空释义。");
            }

            var targetLanguage = NullWhenEmpty(ReadString(root, "target_language")) ?? requestedTargetLanguage;
            return new LookupAnalysisResult(ReadString(root, "detected_language"), targetLanguage,
                NullWhenEmpty(ReadString(root, "source_text")) ?? sourceText,
                NullWhenEmpty(ReadString(root, "source_pinyin")), definition, null, ReadString(root, "english_text"), ReadPronunciations(root),
                NullWhenEmpty(ReadString(root, "general_meaning")), null, NullWhenEmpty(ReadString(root, "internet_meaning")), null,
                ReadStringArray(root, "professional_meanings"), [], provider)
            {
                ContextName = NullWhenEmpty(context),
                ContextDefinition = NullWhenEmpty(ReadString(root, "contextual_definition")),
                ContextEnglishText = NullWhenEmpty(ReadString(root, "contextual_english_text")),
                ContextEnglishPronunciations = ReadPronunciations(root, "contextual_english_pronunciations")
            };
        }
        catch (JsonException)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new TranslationServiceException("翻译 API 返回了空内容。");
            }

            return new LookupAnalysisResult(string.Empty, requestedTargetLanguage, sourceText, null, trimmed, null,
                PhoneticService.ContainsEnglish(sourceText) ? sourceText : string.Empty, [], null, null, null, null, [], [], provider)
            {
                ContextName = NullWhenEmpty(context)
            };
        }
    }

    private static IReadOnlyList<PronunciationOption> ReadPronunciations(JsonElement root, string propertyName = "english_pronunciations")
    {
        if (!root.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<PronunciationOption>();
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var ipa = ReadString(item, "ipa");
            var speakText = ReadString(item, "speak_text");
            if (!string.IsNullOrWhiteSpace(ipa))
            {
                results.Add(new PronunciationOption(ReadString(item, "label"), ipa, speakText,
                    NullWhenEmpty(ReadString(item, "language_code")) ?? "en-US"));
            }
        }

        return results;
    }

    private static string StripMarkdownFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal))
        {
            return value;
        }

        var firstLineEnd = value.IndexOf('\n');
        var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return firstLineEnd >= 0 && lastFence > firstLineEnd ? value[(firstLineEnd + 1)..lastFence].Trim() : value;
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray();
    }

    private static string? NullWhenEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatApiError(HttpStatusCode statusCode, string body)
    {
        var detail = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                detail = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                    ? message.GetString() ?? error.ToString()
                    : error.ToString();
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误正文仍可作为诊断信息。
        }

        detail = detail.Length > 500 ? detail[..500] : detail;
        return $"翻译 API 返回 {(int)statusCode}：{detail}";
    }
}
