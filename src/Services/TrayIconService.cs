using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AITranslator.Interop;

namespace AITranslator.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint IconId = 1;
    private const uint ExitCommandId = 0x1001;
    private const nuint SubclassId = 1;
    private readonly nint _windowHandle;
    private readonly LocalizationService _localization;
    private readonly Action _activate;
    private readonly Action _exit;
    private readonly NativeMethods.SubclassProcedure _subclassProcedure;
    private readonly uint _taskbarCreatedMessage;
    private nint _iconHandle;
    private bool _iconAdded;
    private bool _subclassInstalled;
    private bool _disposed;

    public TrayIconService(nint windowHandle, string iconPath, LocalizationService localization, Action activate, Action exit)
    {
        _windowHandle = windowHandle;
        _localization = localization;
        _activate = activate;
        _exit = exit;
        _subclassProcedure = WindowProcedure;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _iconHandle = NativeMethods.LoadImage(0, iconPath, NativeMethods.ImageIcon, 0, 0,
            NativeMethods.LrLoadFromFile | NativeMethods.LrDefaultSize);
        if (_iconHandle == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载托盘图标。");
        }

        try
        {
            if (!NativeMethods.SetWindowSubclass(_windowHandle, _subclassProcedure, SubclassId, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听托盘图标消息。");
            }

            _subclassInstalled = true;
            AddIcon();
            _localization.LanguageChanged += Localization_LanguageChanged;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AddIcon()
    {
        var data = CreateIconData();
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NimAdd, ref data))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘图标。");
        }

        _iconAdded = true;
    }

    private NativeMethods.NotifyIconData CreateIconData() => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip | NativeMethods.NifShowTip,
        CallbackMessage = NativeMethods.WmTrayIcon,
        Icon = _iconHandle,
        Tip = _localization.TrayTip,
        Info = string.Empty,
        InfoTitle = string.Empty
    };

    private nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam, nuint subclassId,
        nuint referenceData)
    {
        try
        {
            if (message == NativeMethods.WmCommand && (unchecked((uint)wParam) & 0xFFFF) == ExitCommandId)
            {
                _exit();
                return 0;
            }

            if (message == NativeMethods.WmTrayIcon)
            {
                var notification = unchecked((uint)lParam) & 0xFFFF;
                if (notification == NativeMethods.WmLeftButtonUp)
                {
                    _activate();
                    return 0;
                }

                if (notification is NativeMethods.WmRightButtonUp or NativeMethods.WmContextMenu)
                {
                    ShowContextMenu();
                    return 0;
                }
            }

            if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
            {
                _iconAdded = false;
                AddIcon();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        return NativeMethods.DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法获取托盘菜单位置。");
        }

        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘菜单。");
        }

        try
        {
            if (!NativeMethods.AppendMenu(menu, NativeMethods.MfString, ExitCommandId, _localization.Exit))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建托盘退出命令。");
            }

            NativeMethods.SetForegroundWindow(_windowHandle);
            NativeMethods.TrackPopupMenu(menu, NativeMethods.TpmRightButton, cursor.X, cursor.Y, 0, _windowHandle, 0);
            NativeMethods.PostMessage(_windowHandle, NativeMethods.WmNull, 0, 0);
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localization.LanguageChanged -= Localization_LanguageChanged;
        if (_iconAdded)
        {
            var data = CreateIconData();
            NativeMethods.ShellNotifyIcon(NativeMethods.NimDelete, ref data);
            _iconAdded = false;
        }

        if (_subclassInstalled)
        {
            NativeMethods.RemoveWindowSubclass(_windowHandle, _subclassProcedure, SubclassId);
            _subclassInstalled = false;
        }

        if (_iconHandle != 0)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = 0;
        }

        GC.SuppressFinalize(this);
    }

    private void Localization_LanguageChanged(object? sender, EventArgs e)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateIconData();
        NativeMethods.ShellNotifyIcon(NativeMethods.NimModify, ref data);
    }
}
