using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AITranslator.Helpers;
using AITranslator.Interop;
using AITranslator.Models;
using AITranslator.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AITranslator.Windows;

public sealed partial class CaptureOverlayWindow : Window
{
    private const double ToolbarGap = 8;
    private const double HorizontalToolbarWidth = 650;
    private const double HorizontalToolbarHeight = 54;
    private const double VerticalToolbarWidth = 164;
    private const double VerticalToolbarHeight = 274;
    private readonly AppServices _services;
    private readonly string _targetLanguage;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private System.Drawing.Bitmap? _desktopBitmap;
    private System.Drawing.Bitmap? _captureBitmap;
    private global::Windows.Foundation.Point _selectionStart;
    private global::Windows.Foundation.Rect _selectionBounds;
    private global::Windows.Foundation.Rect _toolbarBounds;
    private OcrCaptureResult? _ocrResult;
    private CaptureTranslationResult? _translationResult;
    private double _pixelScaleX = 1;
    private double _pixelScaleY = 1;
    private bool _selecting;
    private bool _showingResult;
    private bool _showingTranslation;

    public CaptureOverlayWindow(AppServices services, string targetLanguage)
    {
        _services = services;
        _targetLanguage = targetLanguage;
        _settings = services.Settings.Current.Copy();
        InitializeComponent();
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), true);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Root_KeyDown), true);
        Root.Loaded += Root_Loaded;
        Closed += CaptureOverlayWindow_Closed;
    }

    public async Task RunAsync()
    {
        var desktopBounds = GetVirtualDesktopBounds();
        _desktopBitmap = CaptureDesktop(desktopBounds);
        DesktopImage.Source = CreateWriteableBitmap(_desktopBitmap);
        ConfigureWindow(desktopBounds);
        Activate();

        var ready = await Task.WhenAny(_loaded.Task, _completion.Task);
        if (ready == _completion.Task)
        {
            return;
        }

        Root.Focus(FocusState.Programmatic);
        await _completion.Task;
    }

    private void ConfigureWindow(System.Drawing.Rectangle desktopBounds)
    {
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        appWindow.MoveAndResize(new RectInt32(desktopBounds.X, desktopBounds.Y, desktopBounds.Width, desktopBounds.Height));
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        AppearanceHelper.Apply(Root, _settings);
        _loaded.TrySetResult(true);
    }

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root).Position;
        if (_showingResult)
        {
            if (!Contains(_selectionBounds, point) && !Contains(_toolbarBounds, point))
            {
                CloseOverlay();
                e.Handled = true;
            }

            return;
        }

        if (IsButtonSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _selectionStart = point;
        _selecting = true;
        Root.CapturePointer(e.Pointer);
        SelectionRectangle.Visibility = Visibility.Visible;
        UpdateSelection(point);
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }

        UpdateSelection(e.GetCurrentPoint(Root).Position);
        e.Handled = true;
    }

    private async void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting || _desktopBitmap is null)
        {
            return;
        }

        var end = e.GetCurrentPoint(Root).Position;
        _selecting = false;
        Root.ReleasePointerCapture(e.Pointer);

        var left = Math.Min(_selectionStart.X, end.X);
        var top = Math.Min(_selectionStart.Y, end.Y);
        var width = Math.Abs(end.X - _selectionStart.X);
        var height = Math.Abs(end.Y - _selectionStart.Y);
        if (width < 12 || height < 12 || Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }

        var scaleX = _desktopBitmap.Width / Root.ActualWidth;
        var scaleY = _desktopBitmap.Height / Root.ActualHeight;
        var pixelLeft = Math.Clamp((int)Math.Round(left * scaleX), 0, _desktopBitmap.Width - 1);
        var pixelTop = Math.Clamp((int)Math.Round(top * scaleY), 0, _desktopBitmap.Height - 1);
        var pixelWidth = Math.Clamp((int)Math.Round(width * scaleX), 1, _desktopBitmap.Width - pixelLeft);
        var pixelHeight = Math.Clamp((int)Math.Round(height * scaleY), 1, _desktopBitmap.Height - pixelTop);
        var pixelRectangle = new System.Drawing.Rectangle(pixelLeft, pixelTop, pixelWidth, pixelHeight);

        _captureBitmap?.Dispose();
        _captureBitmap = _desktopBitmap.Clone(pixelRectangle, PixelFormat.Format32bppArgb);
        _pixelScaleX = pixelRectangle.Width / width;
        _pixelScaleY = pixelRectangle.Height / height;
        _selectionBounds = new global::Windows.Foundation.Rect(left, top, width, height);
        _showingResult = true;
        e.Handled = true;

        try
        {
            await ShowResultAsync(ToPngBytes(_captureBitmap));
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowStatus(exception.Message, false);
        }
    }

    private async Task ShowResultAsync(byte[] image)
    {
        SelectionRectangle.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
        ResultDimmer.Visibility = Visibility.Visible;
        CaptureSurface.Visibility = Visibility.Visible;
        Toolbar.Visibility = Visibility.Visible;
        CaptureSurface.Width = _selectionBounds.Width;
        CaptureSurface.Height = _selectionBounds.Height;
        CaptureSurface.Clip = new RectangleGeometry
        {
            Rect = new global::Windows.Foundation.Rect(0, 0, _selectionBounds.Width, _selectionBounds.Height)
        };
        Canvas.SetLeft(CaptureSurface, _selectionBounds.X);
        Canvas.SetTop(CaptureSurface, _selectionBounds.Y);
        CaptureImage.Source = await CreateBitmapImageAsync(image);
        PositionToolbar();
        AppearanceHelper.Apply(Root, _settings);
        CopyImageButton.IsEnabled = true;

        ShowStatus("正在识别文字…", true);
        _ocrResult = await _services.Ocr.RecognizeLayoutAsync(image, cancellationToken: _cancellation.Token);
        if (_ocrResult.Lines.Count == 0)
        {
            ShowStatus("选区中未识别到文字。", false);
            ShowOriginalButton.IsEnabled = false;
            return;
        }

        _showingTranslation = false;
        RenderTextLayer();
        CopyOriginalButton.IsEnabled = true;
        ShowStatus("正在翻译…", true);
        var targetLanguage = ResolveTargetLanguage(_ocrResult.Text, _targetLanguage);
        _translationResult = await _services.Translator.TranslateCaptureAsync(
            new CaptureTranslationRequest(_ocrResult.Lines.Select(line => line.Text).ToArray(), "auto", targetLanguage), _cancellation.Token);

        _showingTranslation = true;
        RenderTextLayer();
        ShowOriginalButton.IsEnabled = true;
        ShowTranslationButton.IsEnabled = false;
        CopyTranslationButton.IsEnabled = true;
        StatusPanel.Visibility = Visibility.Collapsed;
    }

    private static string ResolveTargetLanguage(string text, string selectedTarget)
    {
        var chineseCount = text.Count(character => character is >= '\u3400' and <= '\u9FFF');
        var latinCount = text.Count(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (string.Equals(selectedTarget, "en", StringComparison.OrdinalIgnoreCase) && latinCount > chineseCount)
        {
            return "zh-CN";
        }

        if (selectedTarget.StartsWith("zh", StringComparison.OrdinalIgnoreCase) && chineseCount > latinCount)
        {
            return "en";
        }

        return selectedTarget;
    }

    private void RenderTextLayer()
    {
        TextLayer.Children.Clear();
        if (_ocrResult is null)
        {
            return;
        }

        for (var index = 0; index < _ocrResult.Lines.Count; index++)
        {
            var source = _ocrResult.Lines[index];
            var text = _showingTranslation && _translationResult is not null ? _translationResult.Lines[index] : source.Text;
            var bounds = ToDisplayBounds(source.Bounds);
            var background = global::Windows.UI.Color.FromArgb(0, 0, 0, 0);
            var foreground = background;
            if (_showingTranslation)
            {
                var sampledColors = SampleColors(source.Bounds);
                background = sampledColors.Background;
                foreground = sampledColors.Foreground;
            }

            var textBox = new TextBox
            {
                AcceptsReturn = true,
                Background = new SolidColorBrush(background),
                BorderThickness = new Thickness(0),
                FontFamily = new FontFamily(_settings.FontFamily),
                FontSize = CalculateOverlayFontSize(text, bounds),
                Foreground = new SolidColorBrush(foreground),
                Height = bounds.Height,
                IsReadOnly = true,
                IsSpellCheckEnabled = false,
                Padding = new Thickness(2, 0, 2, 0),
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                VerticalContentAlignment = VerticalAlignment.Center,
                Width = bounds.Width
            };
            Canvas.SetLeft(textBox, bounds.X);
            Canvas.SetTop(textBox, bounds.Y);
            TextLayer.Children.Add(textBox);
        }
    }

    private global::Windows.Foundation.Rect ToDisplayBounds(global::Windows.Foundation.Rect pixelBounds)
    {
        var left = Math.Max(0, pixelBounds.X / _pixelScaleX - 2);
        var top = Math.Max(0, pixelBounds.Y / _pixelScaleY - 1);
        var width = Math.Min(_selectionBounds.Width - left, pixelBounds.Width / _pixelScaleX + 4);
        var height = Math.Min(_selectionBounds.Height - top, Math.Max(18, pixelBounds.Height / _pixelScaleY + 2));
        return new global::Windows.Foundation.Rect(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    private (global::Windows.UI.Color Background, global::Windows.UI.Color Foreground) SampleColors(global::Windows.Foundation.Rect bounds)
    {
        if (_captureBitmap is null)
        {
            return (global::Windows.UI.Color.FromArgb(238, 255, 255, 255), Microsoft.UI.Colors.Black);
        }

        var left = Math.Clamp((int)Math.Floor(bounds.X), 0, _captureBitmap.Width - 1);
        var top = Math.Clamp((int)Math.Floor(bounds.Y), 0, _captureBitmap.Height - 1);
        var right = Math.Clamp((int)Math.Ceiling(bounds.X + bounds.Width), left + 1, _captureBitmap.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Y + bounds.Height), top + 1, _captureBitmap.Height);
        var stepX = Math.Max(1, (right - left) / 12);
        var stepY = Math.Max(1, (bottom - top) / 6);
        long red = 0;
        long green = 0;
        long blue = 0;
        var count = 0;
        for (var y = top; y < bottom; y += stepY)
        {
            for (var x = left; x < right; x += stepX)
            {
                var pixel = _captureBitmap.GetPixel(x, y);
                red += pixel.R;
                green += pixel.G;
                blue += pixel.B;
                count++;
            }
        }

        var r = (byte)(red / Math.Max(1, count));
        var g = (byte)(green / Math.Max(1, count));
        var b = (byte)(blue / Math.Max(1, count));
        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var foreground = luminance >= 145 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        return (global::Windows.UI.Color.FromArgb(238, r, g, b), foreground);
    }

    private double CalculateOverlayFontSize(string text, global::Windows.Foundation.Rect bounds)
    {
        var estimatedGlyphFactor = text.Any(character => character is >= '\u3400' and <= '\u9fff') ? 0.9 : 0.56;
        var areaFit = Math.Sqrt(bounds.Width * bounds.Height / Math.Max(1, text.Length * estimatedGlyphFactor));
        var heightFit = bounds.Height * 0.72;
        return Math.Clamp(Math.Min(_settings.FontSize, Math.Min(areaFit, heightFit)), 8, 32);
    }

    private void PositionToolbar()
    {
        var spaces = new[]
        {
            (Direction: ToolbarDirection.Below, Available: Root.ActualHeight - _selectionBounds.Bottom - ToolbarGap - 8,
                Required: HorizontalToolbarHeight),
            (Direction: ToolbarDirection.Above, Available: _selectionBounds.Top - ToolbarGap - 8, Required: HorizontalToolbarHeight),
            (Direction: ToolbarDirection.Right, Available: Root.ActualWidth - _selectionBounds.Right - ToolbarGap - 8,
                Required: VerticalToolbarWidth),
            (Direction: ToolbarDirection.Left, Available: _selectionBounds.Left - ToolbarGap - 8, Required: VerticalToolbarWidth)
        };
        var fittingSpaces = spaces.Where(item => item.Available >= item.Required).ToArray();
        var direction = (fittingSpaces.Length > 0 ? fittingSpaces : spaces).MaxBy(item => item.Available / item.Required).Direction;
        var vertical = direction is ToolbarDirection.Left or ToolbarDirection.Right;
        var width = Math.Min(vertical ? VerticalToolbarWidth : HorizontalToolbarWidth, Math.Max(1, Root.ActualWidth - 16));
        var height = Math.Min(vertical ? VerticalToolbarHeight : HorizontalToolbarHeight, Math.Max(1, Root.ActualHeight - 16));
        ToolbarPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        Toolbar.Width = width;
        Toolbar.Height = height;

        double left;
        double top;
        switch (direction)
        {
            case ToolbarDirection.Above:
                left = Clamp(_selectionBounds.X + (_selectionBounds.Width - width) / 2, 8, Root.ActualWidth - width - 8);
                top = Clamp(_selectionBounds.Y - height - ToolbarGap, 8, Root.ActualHeight - height - 8);
                break;
            case ToolbarDirection.Right:
                left = Clamp(_selectionBounds.Right + ToolbarGap, 8, Root.ActualWidth - width - 8);
                top = Clamp(_selectionBounds.Y + (_selectionBounds.Height - height) / 2, 8, Root.ActualHeight - height - 8);
                break;
            case ToolbarDirection.Left:
                left = Clamp(_selectionBounds.X - width - ToolbarGap, 8, Root.ActualWidth - width - 8);
                top = Clamp(_selectionBounds.Y + (_selectionBounds.Height - height) / 2, 8, Root.ActualHeight - height - 8);
                break;
            default:
                left = Clamp(_selectionBounds.X + (_selectionBounds.Width - width) / 2, 8, Root.ActualWidth - width - 8);
                top = Clamp(_selectionBounds.Bottom + ToolbarGap, 8, Root.ActualHeight - height - 8);
                break;
        }

        Canvas.SetLeft(Toolbar, left);
        Canvas.SetTop(Toolbar, top);
        _toolbarBounds = new global::Windows.Foundation.Rect(left, top, width, height);
    }

    private void ShowOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrResult is null || !_showingTranslation)
        {
            return;
        }

        _showingTranslation = false;
        RenderTextLayer();
        ShowOriginalButton.IsEnabled = false;
        ShowTranslationButton.IsEnabled = _translationResult is not null;
    }

    private void ShowTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_translationResult is null || _showingTranslation)
        {
            return;
        }

        _showingTranslation = true;
        RenderTextLayer();
        ShowOriginalButton.IsEnabled = true;
        ShowTranslationButton.IsEnabled = false;
    }

    private void CopyOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrResult is not null)
        {
            CopyText(_ocrResult.Text);
        }
    }

    private void CopyTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_translationResult is not null)
        {
            CopyText(string.Join(Environment.NewLine, _translationResult.Lines));
        }
    }

    private async void CopyImageButton_Click(object sender, RoutedEventArgs e)
    {
        CopyImageButton.IsEnabled = false;
        var statusVisibility = StatusPanel.Visibility;
        try
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            await Task.Yield();
            using var stream = new InMemoryRandomAccessStream();
            if (!_showingTranslation && _captureBitmap is not null)
            {
                using var writer = new DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(ToPngBytes(_captureBitmap));
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            else
            {
                var bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(CaptureSurface);
                var pixels = await bitmap.GetPixelsAsync();
                var pixelBytes = new byte[checked((int)pixels.Length)];
                using (var reader = DataReader.FromBuffer(pixels))
                {
                    reader.ReadBytes(pixelBytes);
                }

                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96,
                    96, pixelBytes);
                await encoder.FlushAsync();
            }

            stream.Seek(0);

            var package = new DataPackage();
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            global::Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (Exception exception)
        {
            ShowStatus($"复制图片失败：{exception.Message}", false);
        }
        finally
        {
            if (StatusPanel.Visibility != Visibility.Visible)
            {
                StatusPanel.Visibility = statusVisibility;
            }

            CopyImageButton.IsEnabled = _ocrResult is not null;
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == global::Windows.System.VirtualKey.Escape)
        {
            CloseOverlay();
            e.Handled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => CloseOverlay();

    private void ShowStatus(string message, bool busy)
    {
        StatusText.Text = message;
        StatusProgress.IsActive = busy;
        StatusProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void UpdateSelection(global::Windows.Foundation.Point current)
    {
        var left = Math.Min(_selectionStart.X, current.X);
        var top = Math.Min(_selectionStart.Y, current.Y);
        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = Math.Abs(current.X - _selectionStart.X);
        SelectionRectangle.Height = Math.Abs(current.Y - _selectionStart.Y);
    }

    private static bool Contains(global::Windows.Foundation.Rect rectangle, global::Windows.Foundation.Point point) =>
        point.X >= rectangle.Left && point.X <= rectangle.Right && point.Y >= rectangle.Top && point.Y <= rectangle.Bottom;

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

    private static double Clamp(double value, double minimum, double maximum) =>
        maximum < minimum ? minimum : Math.Clamp(value, minimum, maximum);

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        global::Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
    }

    private void CloseOverlay()
    {
        _completion.TrySetResult(true);
        Close();
    }

    private void CaptureOverlayWindow_Closed(object sender, WindowEventArgs args)
    {
        _cancellation.Cancel();
        _captureBitmap?.Dispose();
        _captureBitmap = null;
        _desktopBitmap?.Dispose();
        _desktopBitmap = null;
        _completion.TrySetResult(true);
    }

    private static System.Drawing.Rectangle GetVirtualDesktopBounds() => new(NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen), NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));

    private static System.Drawing.Bitmap CaptureDesktop(System.Drawing.Rectangle bounds)
    {
        var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static byte[] ToPngBytes(System.Drawing.Image image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static WriteableBitmap CreateWriteableBitmap(System.Drawing.Bitmap bitmap)
    {
        var image = new WriteableBitmap(bitmap.Width, bitmap.Height);
        var rectangle = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var target = image.PixelBuffer.AsStream();
            var rowLength = bitmap.Width * 4;
            var row = new byte[rowLength];
            var stride = Math.Abs(bitmapData.Stride);
            for (var y = 0; y < bitmap.Height; y++)
            {
                var sourceRow = bitmapData.Stride >= 0 ? y : bitmap.Height - 1 - y;
                Marshal.Copy(IntPtr.Add(bitmapData.Scan0, sourceRow * stride), row, 0, rowLength);
                target.Write(row, 0, rowLength);
            }
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
        }

        image.Invalidate();
        return image;
    }

    private static async Task<BitmapImage> CreateBitmapImageAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private enum ToolbarDirection
    {
        Below,
        Above,
        Right,
        Left
    }
}
