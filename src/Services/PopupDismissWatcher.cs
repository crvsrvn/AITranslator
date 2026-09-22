using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AITranslator.Interop;

namespace AITranslator.Services;

/// <summary>
/// 监听全局键盘与鼠标：按下 Esc、或在弹窗矩形之外按下鼠标键时触发关闭回调，与弹窗是否拥有焦点无关。
/// 钩子安装在构造它的线程上，回调也在该线程的消息循环中执行。
/// </summary>
internal sealed class PopupDismissWatcher : IDisposable
{
    private readonly nint _windowHandle;
    private readonly Action _dismiss;
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly NativeMethods.LowLevelMouseProcedure _mouseProcedure;
    private nint _keyboardHook;
    private nint _mouseHook;

    public PopupDismissWatcher(nint windowHandle, Action dismiss)
    {
        _windowHandle = windowHandle;
        _dismiss = dismiss;
        // 委托保存在字段中，防止钩子存活期间被 GC 回收。
        _keyboardProcedure = KeyboardProcedure;
        _mouseProcedure = MouseProcedure;
        var module = NativeMethods.GetModuleHandle(null);
        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProcedure, module, 0);
        _mouseHook = _keyboardHook == 0 ? 0 : NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseProcedure, module, 0);
        if (_mouseHook == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Dispose()
    {
        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }

        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }
    }

    private nint KeyboardProcedure(int code, nuint wParam, nint lParam)
    {
        try
        {
            if (code == NativeMethods.HcAction && (uint)wParam is NativeMethods.WmKeyDown or NativeMethods.WmSystemKeyDown &&
                Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardInput>(lParam).VirtualKey == NativeMethods.VkEscape)
            {
                _dismiss();
                // 吞掉这次 Esc，避免同时触发前台程序自身的 Esc 行为。
                return 1;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private nint MouseProcedure(int code, nuint wParam, nint lParam)
    {
        try
        {
            if (code == NativeMethods.HcAction && (uint)wParam is NativeMethods.WmLeftButtonDown or NativeMethods.WmRightButtonDown or
                    NativeMethods.WmMiddleButtonDown or NativeMethods.WmXButtonDown &&
                !IsInsideWindow(Marshal.PtrToStructure<NativeMethods.LowLevelMouseInput>(lParam).Point))
            {
                // 窗口外的点击照常传给目标程序，只用来关闭弹窗。
                _dismiss();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private bool IsInsideWindow(NativeMethods.NativePoint point) =>
        NativeMethods.GetWindowRect(_windowHandle, out var rectangle) &&
        point.X >= rectangle.Left && point.X < rectangle.Right && point.Y >= rectangle.Top && point.Y < rectangle.Bottom;
}
