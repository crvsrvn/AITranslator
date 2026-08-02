using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

[assembly: AssemblyTitle("AITranslator")]
[assembly: AssemblyProduct("AITranslator")]
[assembly: AssemblyDescription("AITranslator")]
[assembly: AssemblyCompany("AITranslator")]

internal static class Program
{
    private const int RequiredRuntimeMajor = 10;
    private const int RequiredRuntimeMinor = 0;
    private const string CoreFrameworkName = "Microsoft.NETCore.App";
    private const string DesktopFrameworkName = "Microsoft.WindowsDesktop.App";
    private const string DotNetRuntimeDownloadUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";
    private const string WindowsAppRuntimeBootstrapFileName = "Microsoft.WindowsAppRuntime.Bootstrap.dll";
    private const string WindowsAppRuntimeDownloadUrl = "https://aka.ms/windowsappsdk/2.3/2.3.1/windowsappruntimeinstall-x64.exe";
    private const uint WindowsAppSdkMajorMinorVersion = 0x00020003;
    private const ulong WindowsAppSdkMinimumVersion = 0x0002000300010000UL;

    [STAThread]
    private static int Main(string[] args)
    {
        var applicationDirectory = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Dependencies",
            "App");
        var applicationPath = Path.Combine(applicationDirectory, "AITranslator.exe");
        var bootstrapPath = Path.Combine(applicationDirectory, WindowsAppRuntimeBootstrapFileName);

        if (!File.Exists(applicationPath) || !File.Exists(bootstrapPath))
        {
            return ShowError("Application files are incomplete. Reinstall AITranslator.");
        }

        var isDotNetRuntimeMissing = !HasRequiredDotNetRuntime();
        var isWindowsAppRuntimeMissing = !HasRequiredWindowsAppRuntime(applicationDirectory);
        if (isDotNetRuntimeMissing || isWindowsAppRuntimeMissing)
        {
            return ShowRuntimeInstallationRequired(isDotNetRuntimeMissing, isWindowsAppRuntimeMissing);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = applicationPath,
                Arguments = JoinArguments(args),
                WorkingDirectory = Path.GetDirectoryName(applicationPath),
                UseShellExecute = false
            });
            return 0;
        }
        catch (Exception exception)
        {
            return ShowError("AITranslator failed to start." + Environment.NewLine + exception.Message);
        }
    }

    private static bool HasRequiredDotNetRuntime()
    {
        foreach (var dotNetRoot in GetDotNetRootCandidates())
        {
            if (!IsDotNetInstallation(dotNetRoot))
            {
                continue;
            }

            if (HasCompatibleFramework(dotNetRoot, CoreFrameworkName, "System.Private.CoreLib.dll")
                && HasCompatibleFramework(dotNetRoot, DesktopFrameworkName, "PresentationFramework.dll"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasRequiredWindowsAppRuntime(string applicationDirectory)
    {
        var initialized = false;
        if (!SetDllDirectory(applicationDirectory))
        {
            return false;
        }

        try
        {
            var result = MddBootstrapInitialize2(
                WindowsAppSdkMajorMinorVersion,
                string.Empty,
                WindowsAppSdkMinimumVersion,
                0);
            if (result < 0)
            {
                return false;
            }

            initialized = true;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (initialized)
            {
                try
                {
                    MddBootstrapShutdown();
                }
                catch
                {
                }
            }

            SetDllDirectory(null);
        }
    }

    private static IList<string> GetDotNetRootCandidates()
    {
        var candidates = new List<string>();
        AddCandidate(candidates, Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"));
        AddCandidate(candidates, Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        AddCandidate(candidates, GetRegisteredDotNetRoot());

        var programFiles = Environment.GetEnvironmentVariable("ProgramW6432");
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        var defaultRoot = Path.Combine(programFiles, "dotnet");
        if (IsArm64OperatingSystem())
        {
            AddCandidate(candidates, Path.Combine(defaultRoot, "x64"));
        }

        AddCandidate(candidates, defaultRoot);
        return candidates;
    }

    private static void AddCandidate(ICollection<string> candidates, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (string.Equals(candidate, normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        candidates.Add(normalizedPath);
    }

    private static string GetRegisteredDotNetRoot()
    {
        try
        {
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
            {
                return key == null ? null : key.GetValue("InstallLocation") as string;
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDotNetInstallation(string dotNetRoot)
    {
        return Directory.Exists(Path.Combine(dotNetRoot, "host", "fxr"));
    }

    private static bool HasCompatibleFramework(string dotNetRoot, string frameworkName, string markerFileName)
    {
        var frameworkDirectory = Path.Combine(dotNetRoot, "shared", frameworkName);
        if (!Directory.Exists(frameworkDirectory))
        {
            return false;
        }

        try
        {
            foreach (var versionDirectory in Directory.GetDirectories(frameworkDirectory))
            {
                Version version;
                if (Version.TryParse(Path.GetFileName(versionDirectory), out version)
                    && version.Major == RequiredRuntimeMajor
                    && version.Minor == RequiredRuntimeMinor
                    && File.Exists(Path.Combine(versionDirectory, markerFileName)))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool IsArm64OperatingSystem()
    {
        return string.Equals(
                Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"),
                "ARM64",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432"),
                "ARM64",
                StringComparison.OrdinalIgnoreCase);
    }

    private static int ShowRuntimeInstallationRequired(
        bool isDotNetRuntimeMissing,
        bool isWindowsAppRuntimeMissing)
    {
        var dotNetDownloadStarted = isDotNetRuntimeMissing
            && TryOpenDownload(DotNetRuntimeDownloadUrl);
        var windowsAppDownloadStarted = isWindowsAppRuntimeMissing
            && TryOpenDownload(WindowsAppRuntimeDownloadUrl);
        var message = BuildRuntimeInstallationMessage(
            isDotNetRuntimeMissing,
            isWindowsAppRuntimeMissing,
            dotNetDownloadStarted,
            windowsAppDownloadStarted);

        MessageBox(IntPtr.Zero, message, "AITranslator", 0x30);
        return 1;
    }

    private static string BuildRuntimeInstallationMessage(
        bool isDotNetRuntimeMissing,
        bool isWindowsAppRuntimeMissing,
        bool dotNetDownloadStarted,
        bool windowsAppDownloadStarted)
    {
        var missingCount = (isDotNetRuntimeMissing ? 1 : 0)
            + (isWindowsAppRuntimeMissing ? 1 : 0);
        var startedCount = (dotNetDownloadStarted ? 1 : 0)
            + (windowsAppDownloadStarted ? 1 : 0);
        var message = new StringBuilder();
        message.Append("\u672a\u68c0\u6d4b\u5230\u8fd0\u884c AITranslator \u6240\u9700\u7684\u4ee5\u4e0b\u7ec4\u4ef6\uff1a\r\n");
        if (isDotNetRuntimeMissing)
        {
            message.Append("- .NET 10 Desktop Runtime (x64)\r\n");
        }

        if (isWindowsAppRuntimeMissing)
        {
            message.Append("- Windows App Runtime 2.3.1 (x64)\r\n");
        }

        message.Append("\r\n");
        if (startedCount == missingCount)
        {
            message.Append(missingCount == 2
                ? "\u5df2\u901a\u8fc7\u9ed8\u8ba4\u6d4f\u89c8\u5668\u5f00\u59cb\u4e0b\u8f7d\u8fd9\u4e24\u4e2a\u7ec4\u4ef6\u3002\u8bf7\u5168\u90e8\u5b89\u88c5\u540e\u91cd\u65b0\u542f\u52a8 AITranslator\u3002"
                : "\u5df2\u901a\u8fc7\u9ed8\u8ba4\u6d4f\u89c8\u5668\u5f00\u59cb\u4e0b\u8f7d\u3002\u8bf7\u5b89\u88c5\u540e\u91cd\u65b0\u542f\u52a8 AITranslator\u3002");
        }
        else
        {
            message.Append(startedCount > 0
                ? "\u5df2\u6253\u5f00\u5176\u4e2d\u4e00\u4e2a\u81ea\u52a8\u4e0b\u8f7d\u94fe\u63a5\u3002\u53e6\u4e00\u4e2a\u94fe\u63a5\u65e0\u6cd5\u81ea\u52a8\u6253\u5f00\uff0c\u8bf7\u624b\u52a8\u8bbf\u95ee\uff1a\r\n"
                : "\u65e0\u6cd5\u81ea\u52a8\u6253\u5f00\u5b98\u65b9\u4e0b\u8f7d\u94fe\u63a5\uff0c\u8bf7\u624b\u52a8\u8bbf\u95ee\uff1a\r\n");
            if (isDotNetRuntimeMissing && !dotNetDownloadStarted)
            {
                message.Append("- .NET 10 Desktop Runtime: ");
                message.Append(DotNetRuntimeDownloadUrl);
                message.Append("\r\n");
            }

            if (isWindowsAppRuntimeMissing && !windowsAppDownloadStarted)
            {
                message.Append("- Windows App Runtime 2.3.1: ");
                message.Append(WindowsAppRuntimeDownloadUrl);
                message.Append("\r\n");
            }
        }

        message.Append("\r\n\r\n\u70b9\u51fb\u201c\u786e\u5b9a\u201d\u9000\u51fa\u7a0b\u5e8f\u3002");
        return message.ToString();
    }

    private static bool TryOpenDownload(string downloadUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = downloadUrl,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string JoinArguments(string[] args)
    {
        var result = new StringBuilder();
        for (var index = 0; index < args.Length; index++)
        {
            if (index > 0)
            {
                result.Append(' ');
            }

            AppendQuotedArgument(result, args[index]);
        }

        return result.ToString();
    }

    private static void AppendQuotedArgument(StringBuilder result, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            result.Append(argument);
            return;
        }

        result.Append('"');
        var backslashCount = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashCount * 2 + 1);
                result.Append('"');
            }
            else
            {
                result.Append('\\', backslashCount);
                result.Append(character);
            }

            backslashCount = 0;
        }

        result.Append('\\', backslashCount * 2);
        result.Append('"');
    }

    private static int ShowError(string message)
    {
        MessageBox(IntPtr.Zero, message, "AITranslator", 0x10);
        return 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr windowHandle, string text, string caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string pathName);

    [DllImport(WindowsAppRuntimeBootstrapFileName, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MddBootstrapInitialize2(
        uint majorMinorVersion,
        string versionTag,
        ulong minimumVersion,
        uint options);

    [DllImport(WindowsAppRuntimeBootstrapFileName, ExactSpelling = true)]
    private static extern void MddBootstrapShutdown();
}
