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

    public MainViewModel(AppServices services)
    {
        _services = services;
    }

    public IReadOnlyList<LanguageOption> SourceLanguages => LanguageCatalog.SourceLanguages;

    public IReadOnlyList<LanguageOption> TargetLanguages => LanguageCatalog.TargetLanguages;

    public ObservableCollection<PronunciationOption> DictionaryPronunciations { get; } = [];

    public ObservableCollection<PronunciationOption> AiPronunciations { get; } = [];

    public string SelectedFileDisplayText =>
        string.IsNullOrWhiteSpace(SelectedFilePath) ? "未选择文件" : SelectedFilePath;

    public string FileOutputDisplayText =>
        string.IsNullOrWhiteSpace(FileOutputPath) ? "尚无输出文件" : FileOutputPath;

    public string FileOutputFormatText =>
        string.IsNullOrWhiteSpace(FileOutputPath) ? "PDF · DOCX · PPTX · XLSX" : Path.GetExtension(FileOutputPath).TrimStart('.').ToUpperInvariant();

    public bool HasOutputFile => File.Exists(FileOutputPath);

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SmartTranslateCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty] private string _translatedText = string.Empty;

    [ObservableProperty] private Visibility _lookupResultVisibility = Visibility.Visible;

    [ObservableProperty] private Visibility _textResultVisibility = Visibility.Collapsed;

    [ObservableProperty] private LanguageOption _selectedSourceLanguage = LanguageCatalog.SourceLanguages[0];

    [ObservableProperty] private LanguageOption _selectedTargetLanguage = LanguageCatalog.TargetLanguages[0];

    [ObservableProperty] private string _lookupPhonetic = string.Empty;

    [ObservableProperty] private string _dictionaryText = "暂无词典结果";

    [ObservableProperty] private string _aiMeaningText = "暂无语义结果";

    [ObservableProperty] private string? _lookupAudioUrl;

    [ObservableProperty] private string _selectedFilePath = string.Empty;

    [ObservableProperty] private string _fileOutputPath = string.Empty;

    [ObservableProperty] private double _fileProgress;

    [ObservableProperty] private string _fileProgressText = "尚未选择文件";

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartFileTranslationCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelFileTranslationCommand))]
    private bool _isFileTranslating;

    [ObservableProperty] private string _statusText = "就绪";

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
            StatusText = result.FromCache ? "已从本地缓存读取" : "翻译完成";
        });
    }

    private async Task LookupAsync(string query)
    {
        _lookupCancellation?.Cancel();
        _lookupCancellation?.Dispose();
        _lookupCancellation = new CancellationTokenSource();
        var cancellation = _lookupCancellation;

        IsBusy = true;
        StatusText = "正在读取离线词典";
        LookupPhonetic = string.Empty;
        LookupAudioUrl = null;
        DictionaryText = "正在读取离线词典…";
        AiMeaningText = "AI 正在分析…";
        DictionaryPronunciations.Clear();
        AiPronunciations.Clear();

        var dictionaryTask = _services.Dictionary.LookupEnglishAsync(query, cancellation.Token);
        var aiTask = _services.Translator.LookupAsync(query, "general", cancellation.Token);
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
            DictionaryText = dictionaryError is null ? ResultFormatter.FormatDictionary(dictionary) : $"离线词典不可用：{dictionaryError.Message}";
            ReplacePronunciations(DictionaryPronunciations, dictionary?.Pronunciations ?? []);
            StatusText = "离线词典已显示，AI 正在分析";

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
                throw new InvalidOperationException(aiError?.Message ?? dictionaryError?.Message ?? "未找到查询结果。");
            }

            AiMeaningText = aiResult is null ? $"AI 查词不可用：{aiError?.Message}" : ResultFormatter.FormatLookupAi(aiResult);
            ReplacePronunciations(AiPronunciations, PhoneticService.EnumerateLookupPronunciations(aiResult));
            StatusText = dictionaryError is null && aiError is null ? "查词完成" : "已显示可用的部分结果";
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
        SelectedTargetLanguage = oldSource.Code == "auto"
            ? TargetLanguages.First(item => item.Code == "en")
            : TargetLanguages.FirstOrDefault(item => item.Code == oldSource.Code) ?? TargetLanguages[0];
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
                "general", progress, _fileTranslationCancellation.Token);
            FileOutputPath = report.OutputPath;
            FileProgressText = $"已翻译 {report.TranslatedUnitCount} 个文本单元";
            StatusText = "文件翻译完成";
        }
        catch (OperationCanceledException)
        {
            FileProgressText = "已取消";
            StatusText = "文件翻译已取消；未完成的副本可能保留在源目录。";
        }
        catch (Exception exception)
        {
            FileProgressText = "翻译失败";
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
        OnPropertyChanged(nameof(SelectedFileDisplayText));
        StartFileTranslationCommand.NotifyCanExecuteChanged();
    }

    partial void OnFileOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(FileOutputDisplayText));
        OnPropertyChanged(nameof(FileOutputFormatText));
        OnPropertyChanged(nameof(HasOutputFile));
    }

    partial void OnIsBusyChanged(bool value) =>
        SmartTranslateCommand.NotifyCanExecuteChanged();

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        StatusText = "处理中";
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
}