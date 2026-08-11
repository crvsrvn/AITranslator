using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITranslator.Helpers;
using AITranslator.Models;
using AITranslator.Services;
using Microsoft.UI.Xaml;

namespace AITranslator.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _fileTranslationCancellation;
    private CancellationTokenSource? _lookupCancellation;
    private IReadOnlyList<LanguageOption> _sourceLanguages = [];
    private IReadOnlyList<LanguageOption> _targetLanguages = [];
    private bool _isRefreshingLanguageOptions;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _dictionaryText = Localization.NoDictionaryResult;
        _aiMeaningText = Localization.NoSemanticResult;
        _fileProgressText = Localization.FileNotSelected;
        _statusText = Localization.Ready;
        RefreshLanguageOptions(services.Settings.Current.TextSourceLanguage, services.Settings.Current.TextTargetLanguage);
        Localization.LanguageChanged += Localization_LanguageChanged;
    }

    public LocalizationService Localization => _services.Localization;

    public IReadOnlyList<LanguageOption> AppLanguages => LanguageCatalog.InterfaceLanguages;

    public IReadOnlyList<LanguageOption> SourceLanguages => _sourceLanguages;

    public IReadOnlyList<LanguageOption> TargetLanguages => _targetLanguages;

    public ObservableCollection<PronunciationOption> DictionaryPronunciations { get; } = [];

    public ObservableCollection<PronunciationOption> AiPronunciations { get; } = [];

    public string SelectedFileDisplayText =>
        string.IsNullOrWhiteSpace(SelectedFilePath) ? Localization.NoFileSelected : SelectedFilePath;

    public string FileOutputDisplayText =>
        string.IsNullOrWhiteSpace(FileOutputPath) ? Localization.NoOutputFile : FileOutputPath;

    public string FileOutputFormatText =>
        string.IsNullOrWhiteSpace(FileOutputPath) ? "PDF · DOCX · PPTX · XLSX" : Path.GetExtension(FileOutputPath).TrimStart('.').ToUpperInvariant();

    public bool HasOutputFile => File.Exists(FileOutputPath);

    public bool CanTranslateImages => !IsFileTranslating &&
                                      string.Equals(Path.GetExtension(SelectedFilePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SmartTranslateCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty] private string _translatedText = string.Empty;

    [ObservableProperty] private Visibility _lookupResultVisibility = Visibility.Visible;

    [ObservableProperty] private Visibility _textResultVisibility = Visibility.Collapsed;

    [ObservableProperty] private LanguageOption _selectedSourceLanguage = null!;

    [ObservableProperty] private LanguageOption _selectedTargetLanguage = null!;

    [ObservableProperty] private string _lookupPhonetic = string.Empty;

    [ObservableProperty] private string _dictionaryText = string.Empty;

    [ObservableProperty] private string _aiMeaningText = string.Empty;

    [ObservableProperty] private string? _lookupAudioUrl;

    [ObservableProperty] private string _selectedFilePath = string.Empty;

    [ObservableProperty] private string _fileOutputPath = string.Empty;

    [ObservableProperty] private double _fileProgress;

    [ObservableProperty] private string _fileProgressText = string.Empty;

    private bool _translateImages;

    public bool TranslateImages
    {
        get => _translateImages;
        set => SetProperty(ref _translateImages, value);
    }

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartFileTranslationCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelFileTranslationCommand))]
    private bool _isFileTranslating;

    [ObservableProperty] private string _statusText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanSmartTranslate))]
    private async Task SmartTranslateAsync()
    {
        var query = InputText.Trim();
        if (TranslationInputRouter.ShouldUseLookup(query))
        {
            LookupResultVisibility = Visibility.Visible;
            TextResultVisibility = Visibility.Collapsed;
            await LookupAsync(query);
            return;
        }

        LookupResultVisibility = Visibility.Collapsed;
        TextResultVisibility = Visibility.Visible;
        TranslatedText = string.Empty;
        await RunBusyAsync(async () =>
        {
            var result = await _services.Translator.TranslateAsync(new TranslationRequest(query, SelectedSourceLanguage.Code,
                SelectedTargetLanguage.Code));
            TranslatedText = ResultFormatter.FormatTranslation(result);
            StatusText = result.FromCache ? Localization.LoadedFromCache : Localization.TranslationComplete;
        });
    }

    private async Task LookupAsync(string query)
    {
        _lookupCancellation?.Cancel();
        _lookupCancellation?.Dispose();
        _lookupCancellation = new CancellationTokenSource();
        var cancellation = _lookupCancellation;

        IsBusy = true;
        StatusText = Localization.ReadingOfflineDictionary;
        LookupPhonetic = string.Empty;
        LookupAudioUrl = null;
        DictionaryText = Localization.ReadingOfflineDictionaryEllipsis;
        AiMeaningText = Localization.AiAnalyzing;
        DictionaryPronunciations.Clear();
        AiPronunciations.Clear();

        var dictionaryTask = _services.Dictionary.LookupEnglishAsync(query, cancellation.Token);
        var aiTask = _services.Translator.LookupAsync(query, SelectedSourceLanguage.Code, SelectedTargetLanguage.Code, "general", cancellation.Token);
        DictionaryEntry? dictionary = null;
        LookupAnalysisResult? aiResult = null;
        Exception? dictionaryError = null;
        Exception? aiError = null;

        try
        {
            try
            {
                dictionary = await dictionaryTask;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                dictionaryError = exception;
            }

            LookupPhonetic = dictionary?.Phonetic ?? string.Empty;
            LookupAudioUrl = dictionary?.AudioUrl;
            DictionaryText = dictionaryError is null
                ? ResultFormatter.FormatDictionary(dictionary, Localization)
                : Localization.Format(nameof(LocalizationService.OfflineDictionaryUnavailable), dictionaryError.Message);
            ReplacePronunciations(DictionaryPronunciations, dictionary?.Pronunciations ?? []);
            StatusText = Localization.OfflineDictionaryShown;

            try
            {
                aiResult = await aiTask;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                aiError = exception;
            }

            if (dictionary is null && aiResult is null)
            {
                throw new InvalidOperationException(aiError?.Message ?? dictionaryError?.Message ?? Localization.NoLookupResult);
            }

            AiMeaningText = aiResult is null
                ? Localization.Format(nameof(LocalizationService.AiLookupUnavailable), aiError?.Message)
                : ResultFormatter.FormatLookupAi(aiResult);
            ReplacePronunciations(AiPronunciations, PhoneticService.EnumerateLookupPronunciations(aiResult));
            StatusText = dictionaryError is null && aiError is null ? Localization.LookupComplete : Localization.PartialResults;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_lookupCancellation, cancellation))
            {
                _lookupCancellation.Dispose();
                _lookupCancellation = null;
                IsBusy = false;
            }
        }
    }

    public async Task SpeakCurrentLookupAsync()
    {
        var pronunciation = DictionaryPronunciations.FirstOrDefault() ?? AiPronunciations.FirstOrDefault();
        if (pronunciation is not null)
        {
            await _services.Speech.SpeakPronunciationAsync(pronunciation);
            return;
        }

        if (!string.IsNullOrWhiteSpace(LookupAudioUrl))
        {
            _services.Speech.PlayAudio(LookupAudioUrl);
            return;
        }

        await _services.Speech.SpeakAsync(InputText, PhoneticService.ContainsChinese(InputText) ? "zh-CN" : "en-US");
    }

    public Task SpeakPronunciationAsync(PronunciationOption pronunciation) =>
        _services.Speech.SpeakPronunciationAsync(pronunciation);

    [RelayCommand]
    private async Task SpeakSourceAsync() =>
        await _services.Speech.SpeakAsync(InputText, SelectedSourceLanguage.Code);

    [RelayCommand]
    private async Task SpeakTranslationAsync() =>
        await _services.Speech.SpeakAsync(TranslatedText, SelectedTargetLanguage.Code);

    [RelayCommand]
    private void SwapLanguages()
    {
        var oldSource = SelectedSourceLanguage;
        var oldTarget = SelectedTargetLanguage;
        SelectedSourceLanguage = SourceLanguages.FirstOrDefault(item => item.Code == oldTarget.Code) ?? SourceLanguages[0];
        SelectedTargetLanguage = TargetLanguages.FirstOrDefault(item => item.Code == oldSource.Code) ?? TargetLanguages[0];
        (InputText, TranslatedText) = (TranslatedText, InputText);
    }

    [RelayCommand(CanExecute = nameof(CanStartFileTranslation))]
    private async Task StartFileTranslationAsync()
    {
        _fileTranslationCancellation = new CancellationTokenSource();
        IsFileTranslating = true;
        FileProgress = 0;
        FileOutputPath = string.Empty;
        var progress = new Progress<FileTranslationProgress>(value =>
        {
            FileProgress = value.Percentage;
            FileProgressText = value.Total == 0 ? value.CurrentItem : $"{value.CurrentItem}  {value.Completed}/{value.Total}";
        });

        try
        {
            var report = await _services.Documents.TranslateAsync(SelectedFilePath, SelectedSourceLanguage.Code, SelectedTargetLanguage.Code,
                "general", progress, _fileTranslationCancellation.Token, translateImages: TranslateImages);
            FileOutputPath = report.OutputPath;
            FileProgressText = Localization.Format(nameof(LocalizationService.FileTranslatedUnits), report.TranslatedUnitCount);
            StatusText = Localization.FileTranslationComplete;
        }
        catch (OperationCanceledException)
        {
            FileProgressText = Localization.Canceled;
            StatusText = Localization.FileTranslationCanceled;
        }
        catch (Exception exception)
        {
            FileProgressText = Localization.TranslationFailed;
            StatusText = exception.Message;
        }
        finally
        {
            _fileTranslationCancellation.Dispose();
            _fileTranslationCancellation = null;
            IsFileTranslating = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelFileTranslation))]
    private void CancelFileTranslation() => _fileTranslationCancellation?.Cancel();

    public string GetPreferredSpeechText()
    {
        if (TextResultVisibility == Visibility.Visible && !string.IsNullOrWhiteSpace(TranslatedText))
        {
            return TranslatedText;
        }

        return InputText;
    }

    private bool CanSmartTranslate() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);

    private bool CanStartFileTranslation() => !IsFileTranslating && File.Exists(SelectedFilePath);

    private bool CanCancelFileTranslation() => IsFileTranslating;

    partial void OnSelectedFilePathChanged(string value)
    {
        if (!string.Equals(Path.GetExtension(value), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            TranslateImages = false;
        }

        OnPropertyChanged(nameof(SelectedFileDisplayText));
        OnPropertyChanged(nameof(CanTranslateImages));
        StartFileTranslationCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsFileTranslatingChanged(bool value) => OnPropertyChanged(nameof(CanTranslateImages));

    partial void OnFileOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(FileOutputDisplayText));
        OnPropertyChanged(nameof(FileOutputFormatText));
        OnPropertyChanged(nameof(HasOutputFile));
    }

    partial void OnIsBusyChanged(bool value) =>
        SmartTranslateCommand.NotifyCanExecuteChanged();

    partial void OnSelectedSourceLanguageChanged(LanguageOption value) => PersistTranslationLanguages();

    partial void OnSelectedTargetLanguageChanged(LanguageOption value) => PersistTranslationLanguages();

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        StatusText = Localization.Processing;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void ReplacePronunciations(ObservableCollection<PronunciationOption> target, IEnumerable<PronunciationOption> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private void RefreshLanguageOptions(string sourceLanguage, string targetLanguage)
    {
        _isRefreshingLanguageOptions = true;
        try
        {
            _sourceLanguages = LanguageCatalog.CreateTranslationLanguages(Localization.CurrentLanguage);
            _targetLanguages = LanguageCatalog.CreateTranslationLanguages(Localization.CurrentLanguage);
            OnPropertyChanged(nameof(SourceLanguages));
            OnPropertyChanged(nameof(TargetLanguages));
            SelectedSourceLanguage = _sourceLanguages.First(item => item.Code == LanguageCatalog.NormalizeTranslationLanguage(sourceLanguage));
            SelectedTargetLanguage = _targetLanguages.First(item => item.Code == LanguageCatalog.NormalizeTranslationLanguage(targetLanguage));
        }
        finally
        {
            _isRefreshingLanguageOptions = false;
        }
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        RefreshLanguageOptions(SelectedSourceLanguage.Code, SelectedTargetLanguage.Code);
        OnPropertyChanged(nameof(SelectedFileDisplayText));
        OnPropertyChanged(nameof(FileOutputDisplayText));
        if (!IsBusy && string.IsNullOrWhiteSpace(InputText))
        {
            DictionaryText = Localization.NoDictionaryResult;
            AiMeaningText = Localization.NoSemanticResult;
            StatusText = Localization.Ready;
        }

        if (!IsFileTranslating && string.IsNullOrWhiteSpace(SelectedFilePath))
        {
            FileProgressText = Localization.FileNotSelected;
        }
    }

    private void PersistTranslationLanguages()
    {
        if (_isRefreshingLanguageOptions || SelectedSourceLanguage is null || SelectedTargetLanguage is null)
        {
            return;
        }

        _services.Settings.UpdateTextTranslationLanguages(SelectedSourceLanguage.Code, SelectedTargetLanguage.Code);
        _ = SaveTranslationLanguagesAsync();
    }

    private async Task SaveTranslationLanguagesAsync()
    {
        try
        {
            await _services.Settings.SaveCurrentAsync();
        }
        catch (Exception exception)
        {
            StatusText = Localization.Format(nameof(LocalizationService.SaveLanguageFailed), exception.Message);
        }
    }
}
