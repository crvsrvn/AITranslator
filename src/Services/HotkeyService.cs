using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AITranslator.Interop;
using AITranslator.Models;
using Microsoft.UI.Dispatching;

namespace AITranslator.Services;

public enum HotkeyAction
{
    ToggleMainWindow = 1,
    ShowSelection = 2,
    Speak = 3,
    Capture = 4
}

public sealed class HotkeyService : IDisposable
{
    private const uint DoubleControlIntervalMilliseconds = 300;
    private const uint SupportedModifierMask = NativeMethods.ModAlt | NativeMethods.ModControl | NativeMethods.ModShift |
                                                    NativeMethods.ModWin;
    private readonly Action<HotkeyAction> _callback;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly ManualResetEventSlim _hookInitialized = new(false);
    private readonly NativeMethods.LowLevelKeyboardProcedure _keyboardProcedure;
    private readonly Thread _hookThread;
    private readonly object _registrationGate = new();
    private readonly Dictionary<int, RegisteredHotkey> _registeredHotkeys = [];
    private HotkeyConfiguration _configuration = new([], null);
    private HotkeyConfiguration? _observedConfiguration;
    private RegistrationRequest? _pendingRegistration;
    private Exception? _hookInitializationError;
    private nint _keyboardHook;
    private uint _hookThreadId;
    private uint _lastKeyDown;
    private ControlSide? _pressedControl;
    private ControlSide? _lastTapControl;
    private uint _lastTapTime;
    private bool _currentTapEligible;
    private bool _disposed;

    public HotkeyService(Action<HotkeyAction> callback)
    {
        _callback = callback;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread() ??
                           throw new InvalidOperationException("无法获取主界面调度队列。");
        _keyboardProcedure = KeyboardProcedure;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "AITranslator 全局快捷键"
        };
        _hookThread.Start();
        _hookInitialized.Wait();

        if (_hookInitializationError is not null)
        {
            _hookThread.Join();
            throw new InvalidOperationException("无法启动全局快捷键监听。", _hookInitializationError);
        }
    }

    public IReadOnlyList<string> RegisterAll(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var errors = new List<string>();
        var shortcuts = new List<RegisteredHotkey>();
        var registeredGestures = new HashSet<(uint Modifiers, uint VirtualKey)>();
        HotkeyAction? doubleControlAction = null;

        Register(HotkeyAction.ToggleMainWindow, "主窗口", settings.ToggleWindowShortcut);
        Register(HotkeyAction.ShowSelection, "划词翻译", settings.SelectionShortcut);
        Register(HotkeyAction.Speak, "发音", settings.SpeakShortcut);
        Register(HotkeyAction.Capture, "截屏翻译", settings.CaptureShortcut);

        var registration = ApplyHotkeyRegistrations(shortcuts.ToArray());
        errors.AddRange(registration.Errors);
        Volatile.Write(ref _configuration, new HotkeyConfiguration(registration.FallbackHotkeys, doubleControlAction));
        return errors;

        void Register(HotkeyAction action, string name, string shortcut)
        {
            if (string.IsNullOrWhiteSpace(shortcut))
            {
                return;
            }

            if (!HotkeyGesture.TryParse(shortcut, out var gesture, out var parseError))
            {
                errors.Add($"{name}：{parseError}");
                return;
            }

            if (gesture!.Kind == HotkeyGestureKind.DoubleControl)
            {
                if (doubleControlAction is not null)
                {
                    errors.Add($"{name}：只能有一个操作使用{HotkeyGesture.DoubleControlDisplayText}。");
                    return;
                }

                doubleControlAction = action;
                return;
            }

            var modifiers = gesture.Modifiers & SupportedModifierMask;
            if (!registeredGestures.Add((modifiers, gesture.VirtualKey)))
            {
                errors.Add($"{name}快捷键 {gesture.DisplayText} 与其他操作重复。");
                return;
            }

            shortcuts.Add(new RegisteredHotkey(action, gesture.Modifiers, gesture.VirtualKey, name, gesture.DisplayText));
        }
    }

    private RegistrationOutcome ApplyHotkeyRegistrations(RegisteredHotkey[] shortcuts)
    {
        var request = new RegistrationRequest(shortcuts);
        lock (_registrationGate)
        {
            _pendingRegistration = request;
            if (!NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WmApplyHotkeys, 0, 0))
            {
                _pendingRegistration = null;
                return new RegistrationOutcome(shortcuts, ["无法通知全局快捷键线程更新配置。"]);
            }
        }

        return request.Completed.Wait(TimeSpan.FromSeconds(3))
            ? new RegistrationOutcome(request.FallbackHotkeys, request.Errors)
            : new RegistrationOutcome(shortcuts, ["全局快捷键注册超时。"]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WmQuit, 0, 0);
        }

        _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookInitialized.Dispose();
        GC.SuppressFinalize(this);
    }

    private void HookThreadMain()
    {
        try
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(out _, 0, 0, 0, NativeMethods.PmNoRemove);
            _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProcedure,
                NativeMethods.GetModuleHandle(null), 0);
            if (_keyboardHook == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "全局键盘监听注册失败。");
            }
        }
        catch (Exception exception)
        {
            _hookInitializationError = exception;
            _hookInitialized.Set();
            return;
        }

        _hookInitialized.Set();
        try
        {
            while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Message == NativeMethods.WmApplyHotkeys)
                {
                    ApplyPendingHotkeyRegistration();
                }
                else if (message.Message == NativeMethods.WmHotkey &&
                         _registeredHotkeys.TryGetValue((int)message.WParam, out var hotkey))
                {
                    Dispatch(hotkey.Action);
                }
            }
        }
        finally
        {
            UnregisterCurrentHotkeys();
            if (_keyboardHook != 0)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = 0;
            }
        }
    }

    private void ApplyPendingHotkeyRegistration()
    {
        RegistrationRequest? request;
        lock (_registrationGate)
        {
            request = _pendingRegistration;
            _pendingRegistration = null;
        }

        if (request is null)
        {
            return;
        }

        var errors = new List<string>();
        var fallbackHotkeys = new List<RegisteredHotkey>();
        UnregisterCurrentHotkeys();
        foreach (var hotkey in request.Hotkeys)
        {
            var id = (int)hotkey.Action;
            if (NativeMethods.RegisterHotKey(0, id, hotkey.Modifiers, hotkey.VirtualKey))
            {
                _registeredHotkeys[id] = hotkey;
                continue;
            }

            fallbackHotkeys.Add(hotkey);
        }

        request.FallbackHotkeys = fallbackHotkeys.ToArray();
        request.Errors = errors;
        request.Completed.Set();
    }

    private void UnregisterCurrentHotkeys()
    {
        foreach (var id in _registeredHotkeys.Keys)
        {
            NativeMethods.UnregisterHotKey(0, id);
        }

        _registeredHotkeys.Clear();
    }

    private nint KeyboardProcedure(int code, nuint wParam, nint lParam)
    {
        try
        {
            if (code == NativeMethods.HcAction && !_disposed)
            {
                var message = (uint)wParam;
                if (message is NativeMethods.WmKeyDown or NativeMethods.WmKeyUp or
                    NativeMethods.WmSystemKeyDown or NativeMethods.WmSystemKeyUp)
                {
                    var keyboard = Marshal.PtrToStructure<NativeMethods.LowLevelKeyboardInput>(lParam);
                    if ((keyboard.Flags & NativeMethods.LlkhfInjected) == 0)
                    {
                        ProcessKeyboardInput(keyboard.VirtualKey,
                            message is NativeMethods.WmKeyDown or NativeMethods.WmSystemKeyDown, keyboard.Time);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    // 不自行累积"已按下的键"集合：锁屏、UAC、管理员窗口等场景会丢失抬起事件，残留的键会让双击 Ctrl 一直失效。
    // 修饰键状态改为实时读取系统按键状态，自身只记录可自愈的最小状态。
    private void ProcessKeyboardInput(uint virtualKey, bool isKeyDown, uint time)
    {
        var configuration = Volatile.Read(ref _configuration);
        if (!ReferenceEquals(configuration, _observedConfiguration))
        {
            _observedConfiguration = configuration;
            _lastKeyDown = 0;
            ResetDoubleControlState();
        }

        if (TryGetControlSide(virtualKey, out var control))
        {
            if (isKeyDown)
            {
                ProcessControlKeyDown(control, configuration.DoubleControlAction);
            }
            else
            {
                ProcessControlKeyUp(control, time, configuration.DoubleControlAction);
            }

            return;
        }

        if (!isKeyDown)
        {
            if (_lastKeyDown == virtualKey)
            {
                _lastKeyDown = 0;
            }

            return;
        }

        ResetDoubleControlState();
        // 只有最后按下的键会自动重复，据此过滤重复按下，避免按住快捷键时反复触发。
        if (IsModifierKey(virtualKey) || virtualKey == _lastKeyDown)
        {
            return;
        }

        _lastKeyDown = virtualKey;
        var modifiers = GetPressedModifiers();
        var shortcut = configuration.Shortcuts.FirstOrDefault(item =>
            item.VirtualKey == virtualKey && item.Modifiers == modifiers);
        if (shortcut is not null)
        {
            Dispatch(shortcut.Action);
        }
    }

    private void ProcessControlKeyDown(ControlSide control, HotkeyAction? action)
    {
        if (action is null)
        {
            ResetDoubleControlState();
            return;
        }

        if (_pressedControl == control)
        {
            // 自动重复；若是丢失抬起事件后的再次按下，下一次抬起即可恢复正常。
            return;
        }

        if (_pressedControl is not null)
        {
            _currentTapEligible = false;
            return;
        }

        _pressedControl = control;
        _currentTapEligible = (GetPressedModifiers() & ~NativeMethods.ModControl) == 0;
    }

    private void ProcessControlKeyUp(ControlSide control, uint time, HotkeyAction? action)
    {
        if (action is null || _pressedControl != control || !_currentTapEligible)
        {
            ResetDoubleControlState();
            return;
        }

        _pressedControl = null;
        _currentTapEligible = false;
        // 使用事件自带的时间戳，不受钩子链中其他钩子回调延迟的影响；无符号减法可处理 TickCount 回绕。
        if (_lastTapControl == control && time - _lastTapTime <= DoubleControlIntervalMilliseconds)
        {
            ResetDoubleControlState();
            Dispatch(action.Value);
            return;
        }

        _lastTapControl = control;
        _lastTapTime = time;
    }

    // 当前处理的是非修饰键（或 Ctrl 本身），此前按下的修饰键已反映在系统异步按键状态中。
    private static uint GetPressedModifiers()
    {
        uint modifiers = 0;
        if (IsKeyPressed(NativeMethods.VkControl))
        {
            modifiers |= NativeMethods.ModControl;
        }

        if (IsKeyPressed(NativeMethods.VkAlt))
        {
            modifiers |= NativeMethods.ModAlt;
        }

        if (IsKeyPressed(NativeMethods.VkShift))
        {
            modifiers |= NativeMethods.ModShift;
        }

        if (IsKeyPressed(NativeMethods.VkLeftWindows) || IsKeyPressed(NativeMethods.VkRightWindows))
        {
            modifiers |= NativeMethods.ModWin;
        }

        return modifiers;
    }

    private static bool IsKeyPressed(uint virtualKey) =>
        (NativeMethods.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    private void Dispatch(HotkeyAction action)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                _callback(action);
            }
        });
    }

    private void ResetDoubleControlState()
    {
        _pressedControl = null;
        _lastTapControl = null;
        _lastTapTime = 0;
        _currentTapEligible = false;
    }

    private static bool TryGetControlSide(uint virtualKey, out ControlSide control)
    {
        control = virtualKey == NativeMethods.VkRightControl ? ControlSide.Right : ControlSide.Left;
        return IsControlKey(virtualKey);
    }

    private static bool IsControlKey(uint virtualKey) =>
        virtualKey is NativeMethods.VkControl or NativeMethods.VkLeftControl or NativeMethods.VkRightControl;

    private static bool IsAltKey(uint virtualKey) =>
        virtualKey is NativeMethods.VkAlt or NativeMethods.VkLeftAlt or NativeMethods.VkRightAlt;

    private static bool IsShiftKey(uint virtualKey) =>
        virtualKey is NativeMethods.VkShift or NativeMethods.VkLeftShift or NativeMethods.VkRightShift;

    private static bool IsWindowsKey(uint virtualKey) =>
        virtualKey is NativeMethods.VkLeftWindows or NativeMethods.VkRightWindows;

    private static bool IsModifierKey(uint virtualKey) =>
        IsControlKey(virtualKey) || IsAltKey(virtualKey) || IsShiftKey(virtualKey) || IsWindowsKey(virtualKey);

    private sealed record RegisteredHotkey(HotkeyAction Action, uint Modifiers, uint VirtualKey, string Name,
        string DisplayText);

    private sealed record HotkeyConfiguration(RegisteredHotkey[] Shortcuts, HotkeyAction? DoubleControlAction);

    private sealed record RegistrationOutcome(RegisteredHotkey[] FallbackHotkeys, IReadOnlyList<string> Errors);

    private sealed class RegistrationRequest(RegisteredHotkey[] hotkeys)
    {
        public RegisteredHotkey[] Hotkeys { get; } = hotkeys;

        public ManualResetEventSlim Completed { get; } = new(false);

        public IReadOnlyList<string> Errors { get; set; } = [];

        public RegisteredHotkey[] FallbackHotkeys { get; set; } = [];
    }

    private enum ControlSide
    {
        Left,
        Right
    }
}
