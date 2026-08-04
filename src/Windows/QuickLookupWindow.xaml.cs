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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AITranslator.Windows;

public sealed partial class QuickLookupWindow : Window
{
    private const nuint SubclassId = 1;
    private readonly AppServices _services;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly nint _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly NativeMethods.SubclassProcedure _subclassProcedure;
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
    private bool _escapeCloseScheduled;
    private bool _isClosed;
    private bool _isLoaded;
    private bool _subclassInstalled;
    private DateTimeOffset _ignoreDeactivationUntil = DateTimeOffset.UtcNow.AddMilliseconds(250);

    public QuickLookupWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Root.DataContext = services.Localization;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _subclassProcedure = WindowProcedure;
        ApplyAppearance(services.Settings.Current);
        Root.Loaded += Root_Loaded;
        Activated += QuickLookupWindow_Activated;
        Closed += QuickLookupWindow_Closed;
        ConfigureWindow();
        if (!NativeMethods.SetWindowSubclass(_windowHandle, _subclassProcedure, SubclassId, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), services.Localization.QuickWindowHookFailed);
        }

        _subclassInstalled = true;
        services.Localization.LanguageChanged += Localization_LanguageChanged;
    }

    public async Task ShowLookupAsync(string text)
    {
        var isLookup = ShowLookupShell(text);

        if (!isLookup)
        {
            try
            {
                var translationSettings = _services.Settings.Current;
                var result = await _services.Translator.TranslateAsync(
                    new TranslationRequest(text, translationSettings.TextSourceLanguage, translationSettings.TextTargetLanguage), _lifetime.Token);
                if (_isClosed)
                {
                    return;
                }

                _spokenText = result.Translation;
                _spokenLanguage = ResolveSpeechLanguage(result.Translation, translationSettings.TextTargetLanguage);
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
        var lookupSettings = _services.Settings.Current;
        var aiTask = _services.Translator.LookupAsync(text, lookupSettings.TextSourceLanguage, lookupSettings.TextTargetLanguage, "general", _lifetime.Token);
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
            DictionaryResultText.Text = ResultFormatter.FormatDictionary(dictionary, _services.Localization);
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

        _spokenText = aiResult?.Definition ?? text;
        _spokenLanguage = aiResult?.TargetLanguage ?? lookupSettings.TextTargetLanguage;
        AiResultText.Text = aiResult is not null
            ? ResultFormatter.FormatLookupAi(aiResult)
            : lastError?.Message ?? _services.Localization.AiLookupUnavailableShort;
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
        DictionaryResultText.Text = isLookup ? _services.Localization.ReadingOfflineDictionaryEllipsis : string.Empty;
        AiResultText.Text = isLookup ? _services.Localization.AiAnalyzing : _services.Localization.AiTranslating;
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
        _services.Localization.LanguageChanged -= Localization_LanguageChanged;
        _lifetime.Cancel();
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

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
