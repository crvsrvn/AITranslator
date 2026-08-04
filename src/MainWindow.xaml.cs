using System.Diagnostics;
using AITranslator.Helpers;
using AITranslator.Interop;
using AITranslator.Models;
using AITranslator.Services;
using AITranslator.ViewModels;
using AITranslator.Windows;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace AITranslator;

public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 1180;
    private const int DefaultWindowHeight = 780;
    private const int MinimumRestoredVisibleWidth = 160;
    private const int MinimumRestoredVisibleHeight = 48;
    private readonly AppServices _services;
    private readonly WindowPlacementStore _windowPlacementStore;
    private readonly SelectionService _selectionService = new();
    private readonly nint _windowHandle;
    private readonly HotkeyService _hotkeyService;
    private readonly TrayIconService _trayIconService;
    private QuickLookupWindow? _quickLookupWindow;
    private CaptureOverlayWindow? _captureWindow;
    private bool _isWindowVisible = true;
    private bool _isLoadingSettings;
    private bool _isClosing;
    private TextBox? _pressedControlShortcutBox;
    private TextBox? _lastControlTapShortcutBox;
    private long _lastControlTapTimestamp;
    private bool _controlTapUsedInChord;
    private readonly Dictionary<string, ApiProfileSettings> _apiProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _apiKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _industryContextSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string? _activeApiPresetId;
    private AppWindow? _appWindow;
    private PointInt32? _lastRestoredWindowPosition;

    public MainWindow(AppServices services)
    {
        _services = services;
        _windowPlacementStore = new WindowPlacementStore(services.Paths);
        ViewModel = new MainViewModel(services);
        InitializeComponent();
        Root.DataContext = ViewModel;
        _services.Localization.LanguageChanged += Localization_LanguageChanged;
        ApplyLocalization();
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Root_PreviewKeyDown), true);
        LookupInput.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(LookupInput_KeyDown), true);
        ApiPresetComboBox.ItemsSource = ApiPresetCatalog.Presets;

        Title = "AITranslator";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ConfigureWindow();
        ApplyAppearance(services.Settings.Current);
        Root.Loaded += (_, _) => ApplyAppearance(_services.Settings.Current);
        _industryContextSaveTimer.Tick += IndustryContextSaveTimer_Tick;
        LoadSettingsIntoUiAsync();

        _hotkeyService = new HotkeyService(HandleHotkey);
        ReportHotkeyErrors(_hotkeyService.RegisterAll(services.Settings.Current));
        _trayIconService = new TrayIconService(_windowHandle,
            Path.Combine(AppContext.BaseDirectory, "Assets", "AITranslator.ico"), services.Localization, ShowAndFocus, RequestExitApplication);
        Closed += MainWindow_Closed;

        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    public MainViewModel ViewModel { get; }

    public void ToggleVisibilityAndFocus()
    {
        var isMinimized = NativeMethods.IsIconic(_windowHandle);
        var isForeground = NativeMethods.GetForegroundWindow() == _windowHandle;
        if (_isWindowVisible && NativeMethods.IsWindowVisible(_windowHandle) && !isMinimized && isForeground)
        {
            HideMainWindow();
            return;
        }

        ShowAndFocus();
    }

    private void ShowAndFocus()
    {
        if (_isClosing)
        {
            return;
        }

        var command = NativeMethods.IsIconic(_windowHandle) ? NativeMethods.SwRestore : NativeMethods.SwShow;
        NativeMethods.ShowWindow(_windowHandle, command);
        NativeMethods.SetForegroundWindow(_windowHandle);
        Activate();
        _isWindowVisible = true;
        FocusPrimaryInput();
    }

    private void HideMainWindow()
    {
        NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwHide);
        _isWindowVisible = false;
    }

    private void ExitApplication()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        Close();
    }

    private void RequestExitApplication()
    {
        DispatcherQueue.TryEnqueue(ExitApplication);
    }

    private void ConfigureWindow()
    {
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AITranslator.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        PositionMainWindow(appWindow, new SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
        appWindow.Closing += AppWindow_Closing;
        appWindow.Changed += AppWindow_Changed;
        appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch (Exception)
        {
            SystemBackdrop = null;
        }
    }

    private void PositionMainWindow(AppWindow appWindow, SizeInt32 windowSize)
    {
        var savedPosition = _windowPlacementStore.Load();
        var restoredPosition = savedPosition.GetValueOrDefault();
        var savedBounds = new NativeMethods.NativeRect
        {
            Left = restoredPosition.X,
            Top = restoredPosition.Y,
            Right = restoredPosition.X + windowSize.Width,
            Bottom = restoredPosition.Y + windowSize.Height
        };
        var workArea = default(NativeMethods.NativeRect);
        var restoreSavedPosition = savedPosition is not null &&
                                   TryGetMonitorWorkArea(ref savedBounds, NativeMethods.MonitorDefaultToNull, out workArea) &&
                                   HasSufficientVisibleIntersection(savedBounds, workArea);

        if (!restoreSavedPosition)
        {
            if (!TryGetPrimaryMonitorWorkArea(out workArea))
            {
                appWindow.MoveAndResize(new RectInt32(0, 0, windowSize.Width, windowSize.Height));
                _lastRestoredWindowPosition = new PointInt32(0, 0);
                return;
            }
        }

        var workAreaWidth = Math.Max(1, workArea.Right - workArea.Left);
        var workAreaHeight = Math.Max(1, workArea.Bottom - workArea.Top);
        var width = Math.Min(windowSize.Width, workAreaWidth);
        var height = Math.Min(windowSize.Height, workAreaHeight);
        var x = restoreSavedPosition ? restoredPosition.X : workArea.Left + Math.Max(0, (workAreaWidth - width) / 2);
        var y = restoreSavedPosition ? restoredPosition.Y : workArea.Top + Math.Max(0, (workAreaHeight - height) / 2);
        var maximumX = workArea.Left + Math.Max(0, workAreaWidth - width);
        var maximumY = workArea.Top + Math.Max(0, workAreaHeight - height);
        x = Math.Clamp(x, workArea.Left, maximumX);
        y = Math.Clamp(y, workArea.Top, maximumY);

        if (!NativeMethods.SetWindowPos(_windowHandle, 0, x, y, width, height, NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder))
        {
            appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        _lastRestoredWindowPosition = new PointInt32(x, y);
    }

    private static bool TryGetMonitorWorkArea(ref NativeMethods.NativeRect bounds, uint fallback, out NativeMethods.NativeRect workArea)
    {
        var monitor = NativeMethods.MonitorFromRect(ref bounds, fallback);
        return TryReadMonitorWorkArea(monitor, out workArea);
    }

    private static bool TryGetPrimaryMonitorWorkArea(out NativeMethods.NativeRect workArea)
    {
        var outsideDesktop = new NativeMethods.NativePoint { X = int.MaxValue, Y = int.MaxValue };
        var monitor = NativeMethods.MonitorFromPoint(outsideDesktop, NativeMethods.MonitorDefaultToPrimary);
        return TryReadMonitorWorkArea(monitor, out workArea);
    }

    private static bool TryReadMonitorWorkArea(nint monitor, out NativeMethods.NativeRect workArea)
    {
        var information = new NativeMethods.MonitorInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>()
        };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref information))
        {
            workArea = default;
            return false;
        }

        workArea = information.Work;
        return true;
    }

    private static bool HasSufficientVisibleIntersection(NativeMethods.NativeRect window, NativeMethods.NativeRect workArea)
    {
        var visibleWidth = Math.Max(0, Math.Min(window.Right, workArea.Right) - Math.Max(window.Left, workArea.Left));
        var visibleHeight = Math.Max(0, Math.Min(window.Bottom, workArea.Bottom) - Math.Max(window.Top, workArea.Top));
        var requiredWidth = Math.Min(MinimumRestoredVisibleWidth, Math.Min(window.Right - window.Left, workArea.Right - workArea.Left));
        var requiredHeight = Math.Min(MinimumRestoredVisibleHeight, Math.Min(window.Bottom - window.Top, workArea.Bottom - workArea.Top));
        return visibleWidth >= requiredWidth && visibleHeight >= requiredHeight;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange || sender.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            return;
        }

        UpdateLastRestoredWindowPosition();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        args.Cancel = true;
        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            UpdateLastRestoredWindowPosition();
        }

        HideMainWindow();
    }

    private void UpdateLastRestoredWindowPosition()
    {
        if (NativeMethods.GetWindowRect(_windowHandle, out var rectangle))
        {
            _lastRestoredWindowPosition = new PointInt32(rectangle.Left, rectangle.Top);
        }
    }

    private void FocusPrimaryInput()
    {
        if (LookupPage.Visibility == Visibility.Visible)
        {
            LookupInput.Focus(FocusState.Programmatic);
            return;
        }

        Navigation.SelectedItem = Navigation.MenuItems[0];
        LookupInput.Focus(FocusState.Programmatic);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        LookupPage.Visibility = Visibility.Collapsed;
        FilePage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;

        if (args.IsSettingsSelected)
        {
            SettingsPage.Visibility = Visibility.Visible;
            return;
        }

        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString();
        switch (tag)
        {
            case "file":
                FilePage.Visibility = Visibility.Visible;
                break;
            default:
                LookupPage.Visibility = Visibility.Visible;
                LookupInput.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void HandleHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleMainWindow:
                ToggleVisibilityAndFocus();
                break;
            case HotkeyAction.ShowSelection:
                _ = ShowSelectedTextAsync();
                break;
            case HotkeyAction.Speak:
                if (_quickLookupWindow is not null)
                {
                    _ = _quickLookupWindow.SpeakCurrentAsync();
                }
                else if (LookupPage.Visibility == Visibility.Visible && ViewModel.LookupResultVisibility == Visibility.Visible)
                {
                    _ = ViewModel.SpeakCurrentLookupAsync();
                }
                else
                {
                    _ = _services.Speech.SpeakAsync(ViewModel.GetPreferredSpeechText(), ViewModel.SelectedTargetLanguage.Code);
                }

                break;
            case HotkeyAction.Capture:
                _ = CaptureAndTranslateAsync();
                break;
        }
    }

    private async Task ShowSelectedTextAsync()
    {
        try
        {
            var targetWindow = NativeMethods.GetForegroundWindow();
            var text = await _selectionService.CopySelectedTextAsync(targetWindow);
            if (string.IsNullOrWhiteSpace(text))
            {
                ViewModel.StatusText = _services.Localization.NoSelectedText;
                return;
            }

            _quickLookupWindow?.Close();
            var window = new QuickLookupWindow(_services);
            _quickLookupWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_quickLookupWindow, window))
                {
                    _quickLookupWindow = null;
                }
            };
            await window.ShowLookupAsync(text);
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = exception.Message;
        }
    }

    private async Task CaptureAndTranslateAsync()
    {
        if (_captureWindow is not null)
        {
            _captureWindow.Activate();
            return;
        }

        var restoreMainWindow = _isWindowVisible && NativeMethods.IsWindowVisible(_windowHandle);
        try
        {
            var captureWindow = new CaptureOverlayWindow(_services, ViewModel.SelectedTargetLanguage.Code);
            _captureWindow = captureWindow;
            if (restoreMainWindow && !_isClosing)
            {
                NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwHide);
                _isWindowVisible = false;
                _ = NativeMethods.DwmFlush();
            }

            await captureWindow.RunAsync();
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = exception.Message;
        }
        finally
        {
            _captureWindow = null;
            if (restoreMainWindow)
            {
                NativeMethods.ShowWindow(_windowHandle, NativeMethods.SwShow);
                NativeMethods.SetForegroundWindow(_windowHandle);
                Activate();
                _isWindowVisible = true;
            }
        }
    }

    private async void CaptureButton_Click(object sender, RoutedEventArgs e) =>
        await CaptureAndTranslateAsync();

    internal void StartCaptureForAutomation() =>
        DispatcherQueue.TryEnqueue(() => _ = CaptureAndTranslateAsync());

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != global::Windows.System.VirtualKey.Escape || !_isWindowVisible)
        {
            return;
        }

        e.Handled = true;
        HideMainWindow();
    }

    private void LookupInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Enter && !IsKeyDown(0x10) && !IsKeyDown(0x11) && !IsKeyDown(0x12))
        {
            e.Handled = true;
            ViewModel.InputText = LookupInput.Text;
            if (ViewModel.SmartTranslateCommand.CanExecute(null))
            {
                ViewModel.SmartTranslateCommand.Execute(null);
            }
        }
    }

    private async void ChooseFileButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".pdf");
        picker.FileTypeFilter.Add(".docx");
        picker.FileTypeFilter.Add(".pptx");
        picker.FileTypeFilter.Add(".xlsx");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            ViewModel.SelectedFilePath = file.Path;
            ViewModel.FileProgressText = Path.GetFileName(file.Path);
            ViewModel.FileOutputPath = string.Empty;
            ViewModel.FileProgress = 0;
        }
    }

    private void OpenOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ViewModel.FileOutputPath))
        {
            ViewModel.StatusText = _services.Localization.NoOutputToOpen;
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{ViewModel.FileOutputPath}\"",
            UseShellExecute = true
        });
    }

    private void OpenOutputFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(ViewModel.FileOutputPath))
        {
            ViewModel.StatusText = _services.Localization.NoOutputToOpen;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ViewModel.FileOutputPath,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = _services.Localization.Format(nameof(LocalizationService.CannotOpenOutput), exception.Message);
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsAsync();
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = exception.Message;
        }
    }

    private async void TestApiButton_Click(object sender, RoutedEventArgs e)
    {
        ApiTestInfoBar.IsOpen = false;
        TestApiButton.IsEnabled = false;
        try
        {
            var result = await _services.Translator.TestConnectionAsync(ReadSettingsFromUi(), ApiKeyBox.Password);
            ApiTestInfoBar.Severity = InfoBarSeverity.Success;
            ApiTestInfoBar.Title = _services.Localization.ConnectionSuccess;
            ApiTestInfoBar.Message = _services.Localization.Format(nameof(LocalizationService.ValidResponse), result.Translation);
            ApiTestInfoBar.IsOpen = true;
            ViewModel.StatusText = _services.Localization.ApiTestSuccessUnsaved;
        }
        catch (Exception exception)
        {
            ApiTestInfoBar.Severity = InfoBarSeverity.Error;
            ApiTestInfoBar.Title = _services.Localization.ConnectionFailed;
            ApiTestInfoBar.Message = exception.Message;
            ApiTestInfoBar.IsOpen = true;
            ViewModel.StatusText = exception.Message;
        }
        finally
        {
            TestApiButton.IsEnabled = true;
        }
    }

    private void ApiPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || ApiPresetComboBox.SelectedItem is not ApiPreset preset)
        {
            return;
        }

        CaptureActiveApiProfile();
        _activeApiPresetId = preset.Id;
        ApplyApiProfile(preset, GetOrCreateApiProfile(preset));
        ApiKeyBox.Password = _apiKeys.TryGetValue(preset.Id, out var apiKey) ? apiKey : string.Empty;
        ApiTestInfoBar.IsOpen = false;
        ViewModel.StatusText = _services.Localization.Format(nameof(LocalizationService.ProfileRestored), preset.DisplayName);
    }

    private void ApiModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || ApiPresetComboBox.SelectedItem is not ApiPreset preset)
        {
            return;
        }

        UpdateReasoningOptions(preset, ApiModelBox.SelectedItem?.ToString() ?? ApiModelBox.Text,
            ReadReasoningEffort(TranslationReasoningEffortComboBox), ReadReasoningEffort(FileReasoningEffortComboBox));
    }

    private void ApiModelBox_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (_isLoadingSettings || ApiPresetComboBox.SelectedItem is not ApiPreset preset)
        {
            return;
        }

        UpdateReasoningOptions(preset, args.Text, ReadReasoningEffort(TranslationReasoningEffortComboBox),
            ReadReasoningEffort(FileReasoningEffortComboBox));
    }

    private async void ReasoningEffortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _services.Settings.UpdateReasoningEfforts(ReadReasoningEffort(TranslationReasoningEffortComboBox),
            ReadReasoningEffort(FileReasoningEffortComboBox));
        try
        {
            await _services.Settings.SaveCurrentAsync();
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = _services.Localization.Format(nameof(LocalizationService.SaveReasoningFailed), exception.Message);
        }
    }

    private async void AppLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings || AppLanguageComboBox.SelectedItem is not LanguageOption language)
        {
            return;
        }

        _services.Settings.UpdateAppLanguage(language.Code);
        _services.Localization.SetLanguage(language.Code);
        try
        {
            await _services.Settings.SaveCurrentAsync();
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = _services.Localization.Format(nameof(LocalizationService.SaveLanguageFailed), exception.Message);
        }
    }

    private async void PronunciationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PronunciationOption pronunciation })
        {
            await ViewModel.SpeakPronunciationAsync(pronunciation);
        }
    }

    private void PronunciationButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            ApplyPronunciationTooltip(button);
        }
    }

    private async void ClearAiLookupCacheButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _services.Cache.ClearAiLookupAsync();
            ViewModel.StatusText = _services.Localization.CacheCleared;
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = exception.Message;
        }
    }

    private void ShortcutBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var textBox = (TextBox)sender;
        if (e.Key is global::Windows.System.VirtualKey.Back or global::Windows.System.VirtualKey.Delete)
        {
            textBox.Text = string.Empty;
            ResetShortcutControlCapture();
            e.Handled = true;
            return;
        }

        var key = (int)e.Key;
        if (IsControlKey(key))
        {
            if (_pressedControlShortcutBox is null)
            {
                _pressedControlShortcutBox = textBox;
                _controlTapUsedInChord = false;
            }

            e.Handled = true;
            return;
        }

        if (_pressedControlShortcutBox is not null)
        {
            _controlTapUsedInChord = true;
        }

        if (key is 0x10 or 0x12 or 0x5B or 0x5C)
        {
            e.Handled = true;
            return;
        }

        var control = IsKeyDown(0x11);
        var alt = IsKeyDown(0x12);
        var shift = IsKeyDown(0x10);
        var windows = IsKeyDown(0x5B) || IsKeyDown(0x5C);
        if (HotkeyGesture.TryCreateFromKey(key, control, alt, shift, windows, out var shortcut))
        {
            textBox.Text = shortcut;
        }

        e.Handled = true;
    }

    private void ShortcutBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!IsControlKey((int)e.Key))
        {
            return;
        }

        var textBox = (TextBox)sender;
        if (ReferenceEquals(_pressedControlShortcutBox, textBox) && !_controlTapUsedInChord)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedMilliseconds = (now - _lastControlTapTimestamp) * 1000d / Stopwatch.Frequency;
            if (ReferenceEquals(_lastControlTapShortcutBox, textBox) && elapsedMilliseconds <= 300)
            {
                textBox.Text = HotkeyGesture.DoubleControlDisplayText;
                _lastControlTapShortcutBox = null;
                _lastControlTapTimestamp = 0;
            }
            else
            {
                _lastControlTapShortcutBox = textBox;
                _lastControlTapTimestamp = now;
            }
        }

        _pressedControlShortcutBox = null;
        _controlTapUsedInChord = false;
        e.Handled = true;
    }

    private void FontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var fontSize = Math.Clamp(Math.Round(e.NewValue), 12, 22);
        if (FontSizeValueText is not null)
        {
            FontSizeValueText.Text = fontSize.ToString("0");
        }

        if (!_isLoadingSettings && Root is not null)
        {
            var preview = _services.Settings.Current.Copy();
            preview.FontSize = fontSize;
            ApplyAppearance(preview);
        }
    }

    private async void LoadSettingsIntoUiAsync()
    {
        var settings = _services.Settings.Current;
        _isLoadingSettings = true;
        try
        {
            _apiProfiles.Clear();
            var knownPresetIds = ApiPresetCatalog.Presets.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (settings.ApiProfiles is not null)
            {
                foreach (var pair in settings.ApiProfiles)
                {
                    if (knownPresetIds.Contains(pair.Key) && pair.Value is not null)
                    {
                        _apiProfiles[pair.Key] = pair.Value.Copy();
                    }
                }
            }

            var savedPreset = ApiPresetCatalog.Presets.FirstOrDefault(item =>
                string.Equals(item.Id, settings.ApiPreset, StringComparison.OrdinalIgnoreCase));
            var preset = savedPreset ?? ApiPresetCatalog.Presets[0];
            if (savedPreset is not null && !_apiProfiles.ContainsKey(preset.Id))
            {
                _apiProfiles[preset.Id] = new ApiProfileSettings
                {
                    TranslationEndpoint = settings.TranslationEndpoint,
                    TranslationModel = settings.TranslationModel,
                    ApiKeyHeader = settings.ApiKeyHeader,
                    ApiKeyPrefix = settings.ApiKeyPrefix
                };
            }

            var storedKeys = await _services.Secrets.ReadApiKeysAsync();
            _apiKeys.Clear();
            foreach (var pair in storedKeys)
            {
                if (knownPresetIds.Contains(pair.Key))
                {
                    _apiKeys[pair.Key] = pair.Value;
                }
            }

            if (!_apiKeys.ContainsKey(preset.Id) && storedKeys.TryGetValue(SecretStore.LegacyProfileId, out var legacyApiKey) &&
                !string.IsNullOrWhiteSpace(legacyApiKey))
            {
                _apiKeys[preset.Id] = legacyApiKey;
            }

            _activeApiPresetId = preset.Id;
            ApiPresetComboBox.SelectedItem = preset;
            ApplyApiProfile(preset, GetOrCreateApiProfile(preset));
            ApiKeyBox.Password = _apiKeys.TryGetValue(preset.Id, out var apiKey) ? apiKey : string.Empty;
            GlobalIndustryContextBox.Text = settings.IndustryContext ?? string.Empty;
            AppLanguageComboBox.SelectedItem = LanguageCatalog.InterfaceLanguages.First(item => item.Code == settings.AppLanguage);
            ThemeComboBox.SelectedIndex = settings.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };
            PopulateFontFamilies(settings.FontFamily);
            FontSizeSlider.Value = settings.FontSize;
            ToggleWindowShortcutBox.Text = settings.ToggleWindowShortcut;
            SelectionShortcutBox.Text = settings.SelectionShortcut;
            SpeakShortcutBox.Text = settings.SpeakShortcut;
            CaptureShortcutBox.Text = settings.CaptureShortcut;
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = _services.Localization.Format(nameof(LocalizationService.ReadSettingsFailed), exception.Message);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private AppSettings ReadSettingsFromUi()
    {
        var selectedTheme = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        var selectedFont = (FontFamilyComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Microsoft YaHei UI";
        var preset = ApiPresetComboBox.SelectedItem as ApiPreset ?? ApiPresetCatalog.Presets[0];
        var profile = ReadApiProfileFromUi();
        var profiles = _apiProfiles.ToDictionary(item => item.Key, item => item.Value.Copy(), StringComparer.OrdinalIgnoreCase);
        profiles[preset.Id] = profile.Copy();
        return new AppSettings
        {
            ApiPreset = preset.Id,
            ApiProtocol = preset.Protocol,
            TranslationEndpoint = profile.TranslationEndpoint,
            TranslationModel = profile.TranslationModel,
            TranslationReasoningEffort = ReadReasoningEffort(TranslationReasoningEffortComboBox),
            FileTranslationReasoningEffort = ReadReasoningEffort(FileReasoningEffortComboBox),
            ApiKeyHeader = profile.ApiKeyHeader,
            ApiKeyPrefix = profile.ApiKeyPrefix,
            IndustryContext = GlobalIndustryContextBox.Text.Trim(),
            AppLanguage = (AppLanguageComboBox.SelectedItem as LanguageOption)?.Code ?? _services.Localization.CurrentLanguage,
            TextSourceLanguage = ViewModel.SelectedSourceLanguage.Code,
            TextTargetLanguage = ViewModel.SelectedTargetLanguage.Code,
            ApiProfiles = profiles,
            Theme = selectedTheme,
            FontFamily = selectedFont,
            FontSize = Math.Clamp(Math.Round(FontSizeSlider.Value), 12, 22),
            ToggleWindowShortcut = ToggleWindowShortcutBox.Text,
            SelectionShortcut = SelectionShortcutBox.Text,
            SpeakShortcut = SpeakShortcutBox.Text,
            CaptureShortcut = CaptureShortcutBox.Text
        };
    }

    private void ApplyAppearance(AppSettings settings)
    {
        AppearanceHelper.Apply(Root, settings);
        _quickLookupWindow?.ApplyAppearance(settings);
    }

    private void ApplyLocalization()
    {
        if (Navigation.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = _services.Localization.Settings;
        }
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        ApplyLocalization();
        UpdatePronunciationTooltips(Root);
        if (ApiPresetComboBox.SelectedItem is not ApiPreset preset)
        {
            return;
        }

        UpdateReasoningOptions(preset, GetSelectedApiModel(), ReadReasoningEffort(TranslationReasoningEffortComboBox),
            ReadReasoningEffort(FileReasoningEffortComboBox));
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

    private async Task SaveSettingsAsync()
    {
        CaptureActiveApiProfile();
        var settings = ReadSettingsFromUi();
        var previousSettings = _services.Settings.Current.Copy();
        ValidateShortcuts(settings);
        var errors = _hotkeyService.RegisterAll(settings);
        if (errors.Count > 0)
        {
            WriteHotkeyErrors(errors);
            _hotkeyService.RegisterAll(_services.Settings.Current);
            throw new InvalidOperationException(string.Join("；", errors));
        }

        try
        {
            await _services.Settings.SaveAsync(settings);
            await _services.Secrets.SaveApiKeysAsync(_apiKeys);
        }
        catch
        {
            _hotkeyService.RegisterAll(previousSettings);
            throw;
        }

        ApplyAppearance(settings);
        ViewModel.StatusText = _services.Localization.SettingsSaved;
    }

    private void ValidateShortcuts(AppSettings settings)
    {
        var values = new[]
        {
            settings.ToggleWindowShortcut,
            settings.SelectionShortcut,
            settings.SpeakShortcut,
            settings.CaptureShortcut
        };
        var normalized = new List<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!HotkeyGesture.TryParse(value, out var gesture, out var error))
            {
                throw new InvalidOperationException(error);
            }

            normalized.Add(gesture!.DisplayText);
        }

        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Count)
        {
            throw new InvalidOperationException(_services.Localization.DuplicateHotkeys);
        }
    }

    private void ReportHotkeyErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count > 0)
        {
            WriteHotkeyErrors(errors);
            ViewModel.StatusText = string.Join("；", errors);
        }
    }

    private void WriteHotkeyErrors(IReadOnlyList<string> errors)
    {
        try
        {
            File.AppendAllText(Path.Combine(_services.Paths.LogsDirectory, "hotkey.log"),
                $"{DateTimeOffset.Now:O}  {string.Join("；", errors)}{Environment.NewLine}");
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }
    }

    private void ResetShortcutControlCapture()
    {
        _pressedControlShortcutBox = null;
        _lastControlTapShortcutBox = null;
        _lastControlTapTimestamp = 0;
        _controlTapUsedInChord = false;
    }

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;

    private static bool IsControlKey(int virtualKey) => virtualKey is 0x11 or 0xA2 or 0xA3;

    private void PopulateFontFamilies(string selectedFont)
    {
        FontFamilyComboBox.Items.Clear();
        using var installedFonts = new System.Drawing.Text.InstalledFontCollection();
        foreach (var familyName in installedFonts.Families.Select(family => family.Name).Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
        {
            FontFamilyComboBox.Items.Add(new ComboBoxItem { Content = familyName });
        }

        FontFamilyComboBox.SelectedItem = FontFamilyComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Content?.ToString(), selectedFont, StringComparison.CurrentCultureIgnoreCase));
        FontFamilyComboBox.SelectedIndex = FontFamilyComboBox.SelectedIndex < 0 ? 0 : FontFamilyComboBox.SelectedIndex;
    }

    private void CaptureActiveApiProfile()
    {
        if (string.IsNullOrWhiteSpace(_activeApiPresetId))
        {
            return;
        }

        var preset = ApiPresetCatalog.Presets.FirstOrDefault(item => string.Equals(item.Id, _activeApiPresetId, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
        {
            return;
        }

        _apiProfiles[preset.Id] = ReadApiProfileFromUi();
        if (string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            _apiKeys.Remove(preset.Id);
        }
        else
        {
            _apiKeys[preset.Id] = ApiKeyBox.Password.Trim();
        }
    }

    private ApiProfileSettings ReadApiProfileFromUi()
    {
        var model = GetSelectedApiModel();
        return new ApiProfileSettings
        {
            TranslationEndpoint = ApiEndpointBox.Text.Trim(),
            TranslationModel = model,
            ApiKeyHeader = ApiHeaderBox.Text.Trim(),
            ApiKeyPrefix = ApiPrefixBox.Text.Trim()
        };
    }

    private ApiProfileSettings GetOrCreateApiProfile(ApiPreset preset)
    {
        if (_apiProfiles.TryGetValue(preset.Id, out var profile))
        {
            return profile;
        }

        profile = new ApiProfileSettings
        {
            TranslationEndpoint = preset.Endpoint,
            TranslationModel = preset.Models[0],
            ApiKeyHeader = preset.ApiKeyHeader,
            ApiKeyPrefix = preset.ApiKeyPrefix
        };
        _apiProfiles[preset.Id] = profile;
        return profile;
    }

    private void ApplyApiProfile(ApiPreset preset, ApiProfileSettings profile)
    {
        var wasLoadingSettings = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            ApiEndpointBox.Text = profile.TranslationEndpoint;
            ApiHeaderBox.Text = profile.ApiKeyHeader;
            ApiPrefixBox.Text = profile.ApiKeyPrefix;
            ApiModelBox.ItemsSource = preset.Models;

            var model = string.IsNullOrWhiteSpace(profile.TranslationModel) ? preset.Models[0] : profile.TranslationModel.Trim();
            var knownModel = preset.Models.FirstOrDefault(item => string.Equals(item, model, StringComparison.OrdinalIgnoreCase));
            if (knownModel is null)
            {
                ApiModelBox.SelectedItem = null;
                ApiModelBox.Text = model;
            }
            else
            {
                ApiModelBox.SelectedItem = knownModel;
                ApiModelBox.Text = knownModel;
            }

            var settings = _services.Settings.Current;
            UpdateReasoningOptions(preset, model, settings.TranslationReasoningEffort, settings.FileTranslationReasoningEffort);
        }
        finally
        {
            _isLoadingSettings = wasLoadingSettings;
        }
    }

    private void UpdateReasoningOptions(ApiPreset preset, string model, string translationEffort, string fileTranslationEffort)
    {
        var effortOptions = ApiPresetCatalog.GetReasoningEfforts(preset, model)
            .Select(item => new ReasoningEffortOption(item.Value, GetReasoningEffortDisplayName(item.Value)))
            .ToArray();
        var wasLoadingSettings = _isLoadingSettings;
        _isLoadingSettings = true;
        try
        {
            SetReasoningOptions(TranslationReasoningEffortComboBox, TranslationReasoningPanel, effortOptions, translationEffort,
                preset.DefaultReasoningEffort);
            SetReasoningOptions(FileReasoningEffortComboBox, FileReasoningPanel, effortOptions, fileTranslationEffort, preset.DefaultReasoningEffort);
        }
        finally
        {
            _isLoadingSettings = wasLoadingSettings;
        }
    }

    private static void SetReasoningOptions(ComboBox comboBox, FrameworkElement panel, IReadOnlyList<ReasoningEffortOption> options,
        string requestedEffort, string defaultEffort)
    {
        if (options.Count == 0)
        {
            comboBox.ItemsSource = null;
            comboBox.SelectedItem = null;
            panel.Visibility = Visibility.Collapsed;
            return;
        }

        panel.Visibility = Visibility.Visible;
        comboBox.ItemsSource = options;
        comboBox.SelectedItem = options.FirstOrDefault(item => string.Equals(item.Value, requestedEffort, StringComparison.OrdinalIgnoreCase)) ??
                                options.FirstOrDefault(item => string.Equals(item.Value, defaultEffort, StringComparison.OrdinalIgnoreCase)) ??
                                options[0];
    }

    private static string ReadReasoningEffort(ComboBox comboBox) =>
        (comboBox.SelectedItem as ReasoningEffortOption)?.Value ?? string.Empty;

    private string GetReasoningEffortDisplayName(string value) => value switch
    {
        "off" => _services.Localization.ReasoningOff,
        "low" => _services.Localization.ReasoningLow,
        "medium" => _services.Localization.ReasoningMedium,
        "high" => _services.Localization.ReasoningHigh,
        "xhigh" => _services.Localization.ReasoningVeryHigh,
        "max" => _services.Localization.ReasoningMaximum,
        _ => _services.Localization.AutoReasoning
    };

    private string GetSelectedApiModel()
    {
        if (!string.IsNullOrWhiteSpace(ApiModelBox.Text))
        {
            return ApiModelBox.Text.Trim();
        }

        return ApiModelBox.SelectedItem?.ToString()?.Trim() ?? string.Empty;
    }

    private static void CopyText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(value);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    }

    private void CopyAiMeaningButton_Click(object sender, RoutedEventArgs e) =>
        CopyText(ViewModel.AiMeaningText);

    private void CopyTranslationButton_Click(object sender, RoutedEventArgs e) =>
        CopyText(ViewModel.TranslatedText);

    private void GlobalIndustryContextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _services.Settings.UpdateIndustryContext(GlobalIndustryContextBox.Text);
        _industryContextSaveTimer.Stop();
        _industryContextSaveTimer.Start();
    }

    private async void IndustryContextSaveTimer_Tick(object? sender, object e)
    {
        _industryContextSaveTimer.Stop();
        try
        {
            await _services.Settings.SaveCurrentAsync();
        }
        catch (Exception exception)
        {
            ViewModel.StatusText = _services.Localization.Format(nameof(LocalizationService.SaveContextFailed), exception.Message);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _industryContextSaveTimer.Stop();
        _industryContextSaveTimer.Tick -= IndustryContextSaveTimer_Tick;
        _services.Localization.LanguageChanged -= Localization_LanguageChanged;
        _captureWindow?.Close();
        try
        {
            _services.Settings.SaveCurrentSynchronously();
        }
        catch (Exception exception)
        {
            WriteSettingsError(exception);
        }

        if (_appWindow is not null)
        {
            _appWindow.Closing -= AppWindow_Closing;
            _appWindow.Changed -= AppWindow_Changed;
            if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
            {
                UpdateLastRestoredWindowPosition();
            }
        }

        if (_lastRestoredWindowPosition is PointInt32 position)
        {
            try
            {
                _windowPlacementStore.Save(position);
            }
            catch (Exception exception)
            {
                WriteWindowPlacementError(exception);
            }
        }

        _quickLookupWindow?.Close();
        _trayIconService.Dispose();
        _hotkeyService.Dispose();
        _services.Dispose();
        Application.Current.Exit();
    }

    private void WriteWindowPlacementError(Exception exception)
    {
        try
        {
            File.AppendAllText(Path.Combine(_services.Paths.LogsDirectory, "window-placement.log"),
                $"{DateTimeOffset.Now:O}  {exception}{Environment.NewLine}");
        }
        catch (Exception logException)
        {
            Debug.WriteLine(logException);
        }
    }

    private void WriteSettingsError(Exception exception)
    {
        try
        {
            File.AppendAllText(Path.Combine(_services.Paths.LogsDirectory, "settings.log"),
                $"{DateTimeOffset.Now:O}  {exception}{Environment.NewLine}");
        }
        catch (Exception logException)
        {
            Debug.WriteLine(logException);
        }
    }
}
