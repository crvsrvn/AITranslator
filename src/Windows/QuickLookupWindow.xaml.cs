using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AITranslator.Helpers;
using AITranslator.Interop;
using AITranslator.Models;
using AITranslator.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AITranslator.Windows;

public sealed partial class QuickLookupWindow : Window
{
    private const nuint SubclassId = 1;
    private readonly AppServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly nint _windowHandle;
    private readonly NativeMethods.SubclassProcedure _subclassProcedure;
    private string _spokenText = string.Empty;
    private readonly List<PronunciationOption> _pronunciations = [];
    private bool _closeOnDeactivate;
    private bool _closeScheduled;
    private bool _escapeCloseScheduled;
    private bool _isClosed;
    private bool _isLoaded;
    private bool _subclassInstalled;
    private DateTimeOffset _ignoreDeactivationUntil = DateTimeOffset.UtcNow.AddMilliseconds(250);

    public QuickLookupWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _subclassProcedure = WindowProcedure;
        ApplyAppearance(services.Settings.Current);
        Root.Loaded += Root_Loaded;
        Activated += QuickLookupWindow_Activated;
        Closed += QuickLookupWindow_Closed;
        ConfigureWindow();
        if (!NativeMethods.SetWindowSubclass(_windowHandle, _subclassProcedure, SubclassId, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听划词窗口按键消息。");
        }

        _subclassInstalled = true;
    }

    public async Task ShowLookupAsync(string text)
    {
        var isLookup = ShowLookupShell(text);

        if (!isLookup)
        {
            try
            {
                var targetLanguage = PhoneticService.ContainsChinese(text) ? "en" : "zh-CN";
                var result = await _services.Translator.TranslateAsync(new TranslationRequest(text, "auto", targetLanguage), _lifetime.Token);
                if (_isClosed)
                {
                    return;
                }

                _spokenText = result.Translation;
                AiResultText.Text = ResultFormatter.FormatTranslation(result);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (!_isClosed)
                {
                    AiResultText.Text = exception.Message;
                }
            }
            finally
            {
                if (!_isClosed)
                {
                    BusyRing.IsActive = false;
                }
            }

            return;
        }

        var dictionaryTask = _services.Dictionary.LookupEnglishAsync(text, _lifetime.Token);
        var aiTask = _services.Translator.LookupAsync(text, "general", _lifetime.Token);
        DictionaryEntry? dictionary = null;
        LookupAnalysisResult? aiResult = null;
        Exception? lastError = null;
        try
        {
            dictionary = await dictionaryTask;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lastError = exception;
        }

        if (!_isClosed)
        {
            DictionaryResultText.Text = ResultFormatter.FormatDictionary(dictionary);
            DictionaryPronunciationItems.ItemsSource = dictionary?.Pronunciations;
            if (dictionary is not null)
            {
                _pronunciations.AddRange(dictionary.Pronunciations);
            }
        }

        try
        {
            aiResult = await aiTask;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            lastError = exception;
        }

        if (_isClosed)
        {
            return;
        }

        _spokenText = aiResult?.EnglishText ?? text;
        AiResultText.Text = aiResult is not null
            ? ResultFormatter.FormatLookupAi(aiResult)
            : lastError?.Message ?? "AI 查词不可用。";
        IReadOnlyList<PronunciationOption> aiPronunciations = aiResult is null
            ? []
            : PhoneticService.EnumerateLookupPronunciations(aiResult).ToArray();
        AiPronunciationItems.ItemsSource = aiPronunciations;
        if (aiResult is not null)
        {
            _pronunciations.AddRange(aiPronunciations);
        }

        BusyRing.IsActive = false;
    }

    internal bool ShowLookupShell(string text)
    {
        var isLookup = TranslationInputRouter.ShouldUseLookup(text);
        ConfigureResultMode(isLookup);
        QueryText.Text = text;
        DictionaryResultText.Text = isLookup ? "正在读取离线词典…" : string.Empty;
        AiResultText.Text = isLookup ? "AI 正在分析…" : "AI 正在翻译…";
        DictionaryPronunciationItems.ItemsSource = null;
        AiPronunciationItems.ItemsSource = null;
        _pronunciations.Clear();
        BusyRing.IsActive = true;
        MoveNearCursor();
        Activate();
        return isLookup;
    }

    private void ConfigureResultMode(bool isLookup)
    {
        var dictionaryVisibility = isLookup ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPanel.Visibility = dictionaryVisibility;
        DictionaryDivider.Visibility = dictionaryVisibility;
        AiPronunciationItems.Visibility = dictionaryVisibility;
        DictionaryColumn.Width = isLookup ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(AiPanel, isLookup ? 2 : 0);
        Grid.SetColumnSpan(AiPanel, isLookup ? 1 : 3);
        AiHeading.Text = isLookup ? "AI 结果" : "翻译结果";
    }

    private void ConfigureWindow()
    {
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AITranslator.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        appWindow.Resize(new SizeInt32(720, 420));
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
    }

    private void MoveNearCursor()
    {
        AITranslator.Interop.NativeMethods.GetCursorPos(out var cursor);
        var point = new PointInt32(cursor.X, cursor.Y);
        var displayArea = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        const int width = 720;
        const int height = 420;
        var x = Math.Min(cursor.X + 16, workArea.X + workArea.Width - width);
        var y = Math.Min(cursor.Y + 20, workArea.Y + workArea.Height - height);
        x = Math.Max(x, workArea.X);
        y = Math.Max(y, workArea.Y);

        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        AppWindow.GetFromWindowId(windowId).Move(new PointInt32(x, y));
    }

    public void ApplyAppearance(AppSettings settings)
    {
        AppearanceHelper.Apply(Root, settings);
    }

    internal void DelayCloseOnDeactivate(TimeSpan delay) =>
        _ignoreDeactivationUntil = DateTimeOffset.UtcNow.Add(delay);

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ApplyAppearance(_services.Settings.Current);
        _closeOnDeactivate = true;
    }

    public async Task SpeakCurrentAsync()
    {
        if (_pronunciations.FirstOrDefault() is { } pronunciation)
        {
            await _services.Speech.SpeakPronunciationAsync(pronunciation);
            return;
        }

        await _services.Speech.SpeakAsync(_spokenText, PhoneticService.ContainsChinese(_spokenText) ? "zh-CN" : "en-US");
    }

    private async void PronunciationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PronunciationOption pronunciation })
        {
            await _services.Speech.SpeakPronunciationAsync(pronunciation);
        }
    }

    private nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam, nuint subclassId,
        nuint referenceData)
    {
        try
        {
            if ((message == NativeMethods.WmKeyDown || message == NativeMethods.WmSystemKeyDown) &&
                wParam == (nuint)global::Windows.System.VirtualKey.Escape)
            {
                ScheduleCloseFromEscape();
                return 0;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        return NativeMethods.DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ScheduleCloseFromEscape()
    {
        if (_escapeCloseScheduled)
        {
            return;
        }

        _escapeCloseScheduled = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _escapeCloseScheduled = false;
                if (!_isClosed)
                {
                    Close();
                }
            }))
        {
            _escapeCloseScheduled = false;
        }
    }

    private void QuickLookupWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_isClosed)
        {
            return;
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_isLoaded && _closeOnDeactivate)
            {
                ScheduleCloseAfterDeactivation();
            }

            return;
        }

        _closeOnDeactivate = _isLoaded;
    }

    private void ScheduleCloseAfterDeactivation()
    {
        if (_closeScheduled)
        {
            return;
        }

        _closeScheduled = true;
        _ = CloseAfterDeactivationAsync();
    }

    private async Task CloseAfterDeactivationAsync()
    {
        try
        {
            var delay = _ignoreDeactivationUntil - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _closeScheduled = false;
                if (!_isClosed && AITranslator.Interop.NativeMethods.GetForegroundWindow() != _windowHandle)
                {
                    Close();
                }
            }))
        {
            _closeScheduled = false;
        }
    }

    private void QuickLookupWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _isLoaded = false;
        _closeOnDeactivate = false;
        if (_subclassInstalled)
        {
            NativeMethods.RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
            _subclassInstalled = false;
        }

        Activated -= QuickLookupWindow_Activated;
        Root.Loaded -= Root_Loaded;
        _lifetime.Cancel();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
