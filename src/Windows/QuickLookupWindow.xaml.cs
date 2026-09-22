using System.ComponentModel;
using AITranslator.Helpers;
using AITranslator.Interop;
using AITranslator.Models;
using AITranslator.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AITranslator.Windows;

/// <summary>
/// 划词弹窗。实例常驻复用：热键按下时立即以不抢焦点的方式显示，关闭只是隐藏，避免每次重新构建 XAML 带来的延迟。
/// </summary>
public sealed partial class QuickLookupWindow : Window
{
    private readonly AppServices _services;
    private readonly nint _windowHandle;
    private readonly AppWindow _appWindow;
    private CancellationTokenSource _session = new();
    private PopupDismissWatcher? _dismissWatcher;
    private UIElement? _dragSource;
    private uint? _dragPointerId;
    private NativeMethods.NativePoint _dragCursorOrigin;
    private PointInt32 _dragWindowOrigin;
    private string _spokenText = string.Empty;
    private string _spokenLanguage = "auto";
    private readonly List<PronunciationOption> _pronunciations = [];
    private bool _isLookupMode;
    private bool _closeOnDeactivate;
    private bool _closeScheduled;
    private bool _dismissScheduled;
    private bool _isClosed;
    private bool _isLoaded;
    private DateTimeOffset _ignoreDeactivationUntil;

    public QuickLookupWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Root.DataContext = services.Localization;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        ApplyAppearance(services.Settings.Current);
        Root.Loaded += Root_Loaded;
        Activated += QuickLookupWindow_Activated;
        Closed += QuickLookupWindow_Closed;
        ConfigureWindow();
        services.Localization.LanguageChanged += Localization_LanguageChanged;
    }

    public bool IsShowing { get; private set; }

    /// <summary>
    /// 立即在光标旁显示"正在读取选中文本"的弹窗，返回本次会话的取消令牌；再次热键或关闭弹窗会取消该会话。
    /// </summary>
    public CancellationToken ShowReadingSelection()
    {
        var token = BeginSession();
        ConfigureResultMode(false);
        QueryText.Text = string.Empty;
        DictionaryResultText.Text = string.Empty;
        AiResultText.Text = _services.Localization.ReadingSelectedTextEllipsis;
        return token;
    }

    public void ShowMessage(string message)
    {
        AiResultText.Text = message;
        BusyRing.IsActive = false;
    }

    public async Task ShowLookupAsync(string text)
    {
        // 弹窗已先显示（或在此处显示），翻译/查词全部异步并行进行，各自完成后再回填结果。
        var token = IsShowing ? _session.Token : BeginSession();
        var isLookup = TranslationInputRouter.ShouldUseLookup(text);
        ConfigureResultMode(isLookup);
        QueryText.Text = text;
        DictionaryResultText.Text = isLookup ? _services.Localization.ReadingOfflineDictionaryEllipsis : string.Empty;
        AiResultText.Text = isLookup ? _services.Localization.AiAnalyzing : _services.Localization.AiTranslating;
        var settings = _services.Settings.Current;
        try
        {
            if (isLookup)
            {
                await Task.WhenAll(ShowDictionaryAsync(text, token), ShowAiLookupAsync(text, settings, token));
            }
            else
            {
                await ShowTranslationAsync(text, settings, token);
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                BusyRing.IsActive = false;
            }
        }
    }

    public void Dismiss()
    {
        if (!IsShowing)
        {
            return;
        }

        IsShowing = false;
        _session.Cancel();
        _dismissWatcher?.Dispose();
        _dismissWatcher = null;
        _appWindow.Hide();
    }

    private CancellationToken BeginSession()
    {
        _session.Cancel();
        _session.Dispose();
        _session = new CancellationTokenSource();
        _pronunciations.Clear();
        _spokenText = string.Empty;
        _spokenLanguage = "auto";
        DictionaryPronunciationItems.ItemsSource = null;
        AiPronunciationItems.ItemsSource = null;
        BusyRing.IsActive = true;
        if (!IsShowing)
        {
            try
            {
                _dismissWatcher = new PopupDismissWatcher(_windowHandle, ScheduleDismiss);
            }
            catch (Win32Exception exception)
            {
                throw new Win32Exception(exception.NativeErrorCode, _services.Localization.QuickWindowHookFailed);
            }

            IsShowing = true;
            _ignoreDeactivationUntil = DateTimeOffset.UtcNow.AddMilliseconds(250);
            MoveNearCursor();
            // 不激活窗口：焦点留在目标程序，后续 Ctrl+C 才能发到正确的窗口；Esc/点击关闭由全局钩子负责。
            _appWindow.Show(false);
        }

        return _session.Token;
    }

    private async Task ShowTranslationAsync(string text, AppSettings settings, CancellationToken token)
    {
        try
        {
            var result = await _services.Translator.TranslateAsync(
                new TranslationRequest(text, settings.TextSourceLanguage, settings.TextTargetLanguage), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _spokenText = result.Translation;
            _spokenLanguage = ResolveSpeechLanguage(result.Translation, settings.TextTargetLanguage);
            AiResultText.Text = ResultFormatter.FormatTranslation(result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                AiResultText.Text = exception.Message;
            }
        }
    }

    private async Task ShowDictionaryAsync(string text, CancellationToken token)
    {
        DictionaryEntry? dictionary = null;
        try
        {
            dictionary = await _services.Dictionary.LookupEnglishAsync(text, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            // 离线词典失败时按"未收录"展示，不影响 AI 结果。
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        DictionaryResultText.Text = ResultFormatter.FormatDictionary(dictionary, _services.Localization);
        DictionaryPronunciationItems.ItemsSource = dictionary?.Pronunciations;
        if (dictionary is not null)
        {
            // 词典音标优先于 AI 音标朗读，无论哪个先返回。
            _pronunciations.InsertRange(0, dictionary.Pronunciations);
        }
    }

    private async Task ShowAiLookupAsync(string text, AppSettings settings, CancellationToken token)
    {
        LookupAnalysisResult? aiResult = null;
        string? errorMessage = null;
        try
        {
            aiResult = await _services.Translator.LookupAsync(text, settings.TextSourceLanguage, settings.TextTargetLanguage, "general", token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        _spokenText = aiResult?.Definition ?? text;
        _spokenLanguage = aiResult?.TargetLanguage ?? settings.TextTargetLanguage;
        AiResultText.Text = aiResult is not null
            ? ResultFormatter.FormatLookupAi(aiResult)
            : errorMessage ?? _services.Localization.AiLookupUnavailableShort;
        IReadOnlyList<PronunciationOption> aiPronunciations = aiResult is null
            ? []
            : PhoneticService.EnumerateLookupPronunciations(aiResult).ToArray();
        AiPronunciationItems.ItemsSource = aiPronunciations;
        _pronunciations.AddRange(aiPronunciations);
    }

    private void ConfigureResultMode(bool isLookup)
    {
        _isLookupMode = isLookup;
        var dictionaryVisibility = isLookup ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPanel.Visibility = dictionaryVisibility;
        DictionaryDivider.Visibility = dictionaryVisibility;
        AiPronunciationItems.Visibility = dictionaryVisibility;
        DictionaryColumn.Width = isLookup ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(AiPanel, isLookup ? 2 : 0);
        Grid.SetColumnSpan(AiPanel, isLookup ? 1 : 3);
        AiHeading.Text = isLookup ? _services.Localization.AiResult : _services.Localization.TranslationResult;
    }

    private void ConfigureWindow()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AITranslator.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }

        _appWindow.Resize(new SizeInt32(720, 420));
        _appWindow.IsShownInSwitchers = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
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

        _appWindow.Move(new PointInt32(x, y));
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

        await _services.Speech.SpeakAsync(_spokenText, ResolveSpeechLanguage(_spokenText, _spokenLanguage));
    }

    private async void PronunciationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PronunciationOption pronunciation })
        {
            await _services.Speech.SpeakPronunciationAsync(pronunciation);
        }
    }

    private void PronunciationButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ApplyPronunciationTooltip(button);
        }
    }

    private void DragRegion_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var dragSource = (UIElement)sender;
        if (_dragPointerId is not null || !e.GetCurrentPoint(dragSource).Properties.IsLeftButtonPressed ||
            IsButtonSource(e.OriginalSource as DependencyObject) ||
            !NativeMethods.GetCursorPos(out var cursor) || !NativeMethods.GetWindowRect(_windowHandle, out var windowRectangle) ||
            !dragSource.CapturePointer(e.Pointer))
        {
            return;
        }

        _dragSource = dragSource;
        _dragPointerId = e.Pointer.PointerId;
        _dragCursorOrigin = cursor;
        _dragWindowOrigin = new PointInt32(windowRectangle.Left, windowRectangle.Top);
        e.Handled = true;
    }

    private void DragRegion_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId != e.Pointer.PointerId)
        {
            return;
        }

        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            StopDragging(e);
            return;
        }

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            _appWindow.Move(new PointInt32(
                _dragWindowOrigin.X + cursor.X - _dragCursorOrigin.X,
                _dragWindowOrigin.Y + cursor.Y - _dragCursorOrigin.Y));
        }

        e.Handled = true;
    }

    private void DragRegion_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId == e.Pointer.PointerId)
        {
            StopDragging(e);
            e.Handled = true;
        }
    }

    private void DragRegion_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId == e.Pointer.PointerId)
        {
            StopDragging(e);
        }
    }

    private void DragRegion_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_dragPointerId == e.Pointer.PointerId)
        {
            ClearDragState();
        }
    }

    private void StopDragging(PointerRoutedEventArgs e)
    {
        var dragSource = _dragSource;
        ClearDragState();
        dragSource?.ReleasePointerCapture(e.Pointer);
    }

    private void ClearDragState()
    {
        _dragSource = null;
        _dragPointerId = null;
    }

    private static bool IsButtonSource(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Button)
            {
                return true;
            }
        }

        return false;
    }

    // 钩子回调中不直接操作窗口，统一排队到调度器后再隐藏。
    private void ScheduleDismiss()
    {
        if (_dismissScheduled)
        {
            return;
        }

        _dismissScheduled = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _dismissScheduled = false;
                Dismiss();
            }))
        {
            _dismissScheduled = false;
        }
    }

    private void QuickLookupWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_isClosed || !IsShowing)
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
        var token = _session.Token;
        try
        {
            var delay = _ignoreDeactivationUntil - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _closeScheduled = false;
            return;
        }

        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _closeScheduled = false;
                if (IsShowing && AITranslator.Interop.NativeMethods.GetForegroundWindow() != _windowHandle)
                {
                    Dismiss();
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
        IsShowing = false;
        _dismissWatcher?.Dispose();
        _dismissWatcher = null;
        Activated -= QuickLookupWindow_Activated;
        Root.Loaded -= Root_Loaded;
        _services.Localization.LanguageChanged -= Localization_LanguageChanged;
        _session.Cancel();
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        AiHeading.Text = _isLookupMode ? _services.Localization.AiResult : _services.Localization.TranslationResult;
        UpdatePronunciationTooltips(Root);
    }

    private void UpdatePronunciationTooltips(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Button { Tag: PronunciationOption } button)
            {
                ApplyPronunciationTooltip(button);
            }

            UpdatePronunciationTooltips(child);
        }
    }

    private void ApplyPronunciationTooltip(Button button)
    {
        var isDictionaryButton = IsDescendantOf(button, DictionaryPronunciationItems);
        ToolTipService.SetToolTip(button, isDictionaryButton ? _services.Localization.PronounceIpa : _services.Localization.Pronounce);
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveSpeechLanguage(string text, string requestedLanguage)
    {
        if (!string.Equals(requestedLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return requestedLanguage;
        }

        return PhoneticService.ContainsChinese(text) ? "zh-CN" : "en-US";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Dismiss();
}
