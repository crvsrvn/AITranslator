namespace AITranslator.Services;

public sealed class AppServices : IDisposable
{
    private AppServices(HttpClient httpClient, AppPaths paths, SettingsService settings, LocalizationService localization, SecretStore secrets,
        TranslationCache cache, PhoneticService phonetics, OpenDictionaryService dictionary, TranslationOrchestrator translator, SpeechService speech)
    {
        HttpClient = httpClient;
        Paths = paths;
        Settings = settings;
        Localization = localization;
        Secrets = secrets;
        Cache = cache;
        Phonetics = phonetics;
        Dictionary = dictionary;
        Translator = translator;
        Speech = speech;
    }

    public HttpClient HttpClient { get; }

    public AppPaths Paths { get; }

    public SettingsService Settings { get; }

    public LocalizationService Localization { get; }

    public SecretStore Secrets { get; }

    public TranslationCache Cache { get; }

    public PhoneticService Phonetics { get; }

    public OpenDictionaryService Dictionary { get; }

    public TranslationOrchestrator Translator { get; }

    public SpeechService Speech { get; }

    public DocumentTranslationService Documents { get; private set; } = null!;

    public OcrService Ocr { get; private set; } = null!;

    public static async Task<AppServices> CreateAsync(CancellationToken cancellationToken = default)
    {
        var paths = new AppPaths();
        var settings = new SettingsService(paths);
        var secrets = new SecretStore(paths);
        var cache = new TranslationCache(paths);
        var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(90)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AITranslator/1.0");

        await settings.LoadAsync(cancellationToken);
        var localization = new LocalizationService(settings.Current.AppLanguage);

        var phonetics = new PhoneticService();
        var provider = new OpenAiCompatibleTranslationProvider(httpClient);
        var translator = new TranslationOrchestrator(provider, secrets, settings, cache, phonetics);
        var services = new AppServices(httpClient, paths, settings, localization, secrets, cache, phonetics, new OpenDictionaryService(paths, phonetics),
            translator, new SpeechService());

        services.Documents = new DocumentTranslationService(translator);
        services.Ocr = new OcrService();
        return services;
    }

    public void Dispose()
    {
        Speech.Dispose();
        Phonetics.Dispose();
        HttpClient.Dispose();
    }
}
