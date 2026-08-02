using System.Text.Json;
using AITranslator.Models;

namespace AITranslator.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private long _industryContextRevision;
    private long _reasoningEffortRevision;

    public SettingsService(AppPaths paths)
    {
        _paths = paths;
    }

    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.SettingsFile))
        {
            Current = new AppSettings();
            return;
        }

        await using var stream = File.OpenRead(_paths.SettingsFile);
        Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
        NormalizeReasoningEfforts(Current);
        if (string.Equals(Current.CaptureShortcut, "Ctrl+Alt+A", StringComparison.OrdinalIgnoreCase))
        {
            Current.CaptureShortcut = "Ctrl+Shift+A";
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var revision = Volatile.Read(ref _industryContextRevision);
        var reasoningRevision = Volatile.Read(ref _reasoningEffortRevision);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = settings.Copy();
            var temporaryFile = _paths.SettingsFile + ".tmp";
            await using (var stream = File.Create(temporaryFile))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryFile, _paths.SettingsFile, true);
            if (revision != Volatile.Read(ref _industryContextRevision))
            {
                snapshot.IndustryContext = Current.IndustryContext;
            }

            if (reasoningRevision != Volatile.Read(ref _reasoningEffortRevision))
            {
                snapshot.TranslationReasoningEffort = Current.TranslationReasoningEffort;
                snapshot.FileTranslationReasoningEffort = Current.FileTranslationReasoningEffort;
            }

            Current = snapshot;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    public void UpdateIndustryContext(string value)
    {
        var settings = Current.Copy();
        settings.IndustryContext = value.Trim();
        Current = settings;
        Interlocked.Increment(ref _industryContextRevision);
    }

    public void UpdateReasoningEfforts(string translationEffort, string fileTranslationEffort)
    {
        var settings = Current.Copy();
        settings.TranslationReasoningEffort = translationEffort;
        settings.FileTranslationReasoningEffort = fileTranslationEffort;
        Current = settings;
        Interlocked.Increment(ref _reasoningEffortRevision);
    }

    public Task SaveCurrentAsync(CancellationToken cancellationToken = default) =>
        SaveAsync(Current.Copy(), cancellationToken);

    public void SaveCurrentSynchronously()
    {
        _saveGate.Wait();
        try
        {
            var snapshot = Current.Copy();
            var temporaryFile = _paths.SettingsFile + ".tmp";
            using (var stream = File.Create(temporaryFile))
            {
                JsonSerializer.Serialize(stream, snapshot, JsonOptions);
            }

            File.Move(temporaryFile, _paths.SettingsFile, true);
            Current = snapshot;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static void NormalizeReasoningEfforts(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ReasoningEffort))
        {
            settings.TranslationReasoningEffort = settings.ReasoningEffort;
            settings.FileTranslationReasoningEffort = settings.ReasoningEffort;
        }

        settings.TranslationReasoningEffort = string.IsNullOrWhiteSpace(settings.TranslationReasoningEffort)
            ? "medium"
            : settings.TranslationReasoningEffort.Trim();
        settings.FileTranslationReasoningEffort = string.IsNullOrWhiteSpace(settings.FileTranslationReasoningEffort)
            ? settings.TranslationReasoningEffort
            : settings.FileTranslationReasoningEffort.Trim();
        settings.ReasoningEffort = null;
        settings.ActiveReasoningEffort = string.Empty;
    }
}
