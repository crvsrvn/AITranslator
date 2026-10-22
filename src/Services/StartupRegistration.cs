using Microsoft.Win32;

namespace AITranslator.Services;

/// <summary>通过当前用户的 Run 注册表项管理开机自动运行；注册表即唯一状态源，不写入 settings.json。</summary>
public static class StartupRegistration
{
    public const string AutostartArgument = "--autostart";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // 任务管理器“启动”页写入的启用状态；首字节为奇数表示已禁用（非官方文档约定）。
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string ValueName = "AITranslator";

    // 整理布局下指向根目录启动器（先检查运行时再转发参数），开发布局下即程序自身。
    private static string Command => $"\"{Path.Combine(AppPaths.AppRootDirectory, "AITranslator.exe")}\" {AutostartArgument}";

    public static bool IsAutostartLaunch =>
        Environment.GetCommandLineArgs().Skip(1).Contains(AutostartArgument, StringComparer.OrdinalIgnoreCase);

    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (!string.Equals(runKey?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath);
        return approvedKey?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using (var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (enabled)
            {
                runKey.SetValue(ValueName, Command);
            }
            else
            {
                runKey.DeleteValue(ValueName, false);
            }
        }

        // 清除任务管理器留下的禁用标记，无标记时系统视为启用。
        using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, true);
        approvedKey?.DeleteValue(ValueName, false);
    }
}
