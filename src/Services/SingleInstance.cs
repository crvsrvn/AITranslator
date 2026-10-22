using System.ComponentModel;
using System.Diagnostics;
using AITranslator.Interop;

namespace AITranslator.Services;

/// <summary>单实例保护：新进程启动时让同一会话内已有的实例退出，由新进程接管。</summary>
public static class SingleInstance
{
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(1);
    private static readonly uint ExitRequestMessage = NativeMethods.RegisterWindowMessage("AITranslator.ExitRequest");

    // 注册失败时返回 0，与 WM_NULL 相同，必须排除。
    public static bool IsExitRequest(uint message) => message != 0 && message == ExitRequestMessage;

    /// <summary>广播退出请求并等待已有实例自行退出；超时或无法响应（如旧版本）则强制结束。</summary>
    public static async Task CloseOtherInstancesAsync()
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(current.ProcessName);
        try
        {
            var others = processes.Where(process => process.Id != current.Id && process.SessionId == current.SessionId).ToList();
            if (others.Count == 0)
            {
                return;
            }

            NativeMethods.PostMessage(NativeMethods.HwndBroadcast, ExitRequestMessage, 0, 0);
            using var timeout = new CancellationTokenSource(GracefulExitTimeout);
            foreach (var process in others)
            {
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    Kill(process);
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill();
            // 等进程真正结束再继续，确保其热键已释放。
            process.WaitForExit(KillTimeout);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // 如对方以管理员权限运行而无法结束，只能继续启动。
            Debug.WriteLine(exception);
        }
    }
}
