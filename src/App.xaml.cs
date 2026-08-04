using AITranslator.Interop;
using AITranslator.Services;
using AITranslator.Windows;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Text;

namespace AITranslator;

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string ApplicationUserModelId = "AITranslator.Desktop";
    private static QuickLookupWindow? _automationWindow;

    public static AppServices Services { get; private set; } = null!;

    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        var result = NativeMethods.SetCurrentProcessExplicitAppUserModelID(ApplicationUserModelId);
        if (result < 0)
        {
            System.Diagnostics.Debug.WriteLine($"设置 AppUserModelID 失败：0x{result:X8}");
        }

        InitializeComponent();
        UnhandledException += App_UnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = await AppServices.CreateAsync();
            var automationLookupText = Environment.GetEnvironmentVariable("AITRANSLATOR_AUTOMATION_QUICK_LOOKUP_TEXT");
            if (!string.IsNullOrWhiteSpace(automationLookupText))
            {
                MainWindow = new MainWindow(Services);
                MainWindow.Activate();
                var quickLookupWindow = new QuickLookupWindow(Services);
                _automationWindow = quickLookupWindow;
                quickLookupWindow.DelayCloseOnDeactivate(TimeSpan.FromSeconds(3));
                quickLookupWindow.Closed += (_, _) => _automationWindow = null;
                _ = quickLookupWindow.ShowLookupAsync(automationLookupText);
                return;
            }

            MainWindow = new MainWindow(Services);
            MainWindow.Activate();
            if (string.Equals(Environment.GetEnvironmentVariable("AITRANSLATOR_AUTOMATION_CAPTURE"), "1",
                    StringComparison.Ordinal))
            {
                MainWindow.StartCaptureForAutomation();
            }
        }
        catch (Exception exception)
        {
            WriteStartupErrorLog(exception);
            NativeMethods.MessageBox(0, $"AITranslator 启动失败：{exception.Message}", "AITranslator", 0x10);
            Exit();
        }
    }

    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        WriteRuntimeErrorLog(args.Exception);
    }

    private static void WriteRuntimeErrorLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(AppPaths.AppRootDirectory, "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "runtime-error.log"),
                $"{DateTimeOffset.Now:O}  HRESULT: 0x{exception.HResult:X8}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logException)
        {
            System.Diagnostics.Debug.WriteLine(logException);
        }
    }

    private static void WriteStartupErrorLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(AppPaths.AppRootDirectory, "Logs");
            Directory.CreateDirectory(directory);
            var details = new StringBuilder().AppendLine($"{DateTimeOffset.Now:O}").AppendLine($"HRESULT: 0x{exception.HResult:X8}")
                .AppendLine(exception.ToString());

            AppendRestrictedErrorInfo(details);
            File.WriteAllText(Path.Combine(directory, "startup-error.log"), details.ToString());
        }
        catch (Exception logException)
        {
            System.Diagnostics.Debug.WriteLine(logException);
        }
    }

    private static void AppendRestrictedErrorInfo(StringBuilder details)
    {
        var result = GetRestrictedErrorInfo(out var errorInfoPointer);
        if (result < 0 || errorInfoPointer == IntPtr.Zero)
        {
            details.AppendLine($"RestrictedErrorInfo: unavailable (0x{result:X8})");
            return;
        }

        object? errorInfoObject = null;
        try
        {
            errorInfoObject = Marshal.GetObjectForIUnknown(errorInfoPointer);
            var errorInfo = (IRestrictedErrorInfo)errorInfoObject;
            var detailsResult = errorInfo.GetErrorDetails(out var description, out var error, out var restrictedDescription, out var capabilitySid);
            var referenceResult = errorInfo.GetReference(out var reference);

            details.AppendLine($"RestrictedErrorInfo HRESULT: 0x{detailsResult:X8}").AppendLine($"Restricted error: 0x{error:X8}")
                .AppendLine($"Description: {description}").AppendLine($"Restricted description: {restrictedDescription}")
                .AppendLine($"Capability SID: {capabilitySid}").AppendLine($"Reference HRESULT: 0x{referenceResult:X8}")
                .AppendLine($"Reference: {reference}");
        }
        catch (Exception exception)
        {
            details.AppendLine($"RestrictedErrorInfo read failed: {exception}");
        }
        finally
        {
            if (errorInfoObject is not null && Marshal.IsComObject(errorInfoObject))
            {
                Marshal.FinalReleaseComObject(errorInfoObject);
            }

            Marshal.Release(errorInfoPointer);
        }
    }

    [DllImport("combase.dll")]
    private static extern int GetRestrictedErrorInfo(out IntPtr restrictedErrorInfo);

    [ComImport]
    [Guid("82BA7092-4C88-427D-A7BC-16DD93FEB67E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRestrictedErrorInfo
    {
        [PreserveSig]
        int GetErrorDetails([MarshalAs(UnmanagedType.BStr)] out string description, out int error,
            [MarshalAs(UnmanagedType.BStr)] out string restrictedDescription, [MarshalAs(UnmanagedType.BStr)] out string capabilitySid);

        [PreserveSig]
        int GetReference([MarshalAs(UnmanagedType.BStr)] out string reference);
    }
}
