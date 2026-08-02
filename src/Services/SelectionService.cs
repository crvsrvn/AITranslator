using System.Runtime.InteropServices;
using AITranslator.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace AITranslator.Services;

public sealed class SelectionService
{
    public Task<string?> CopySelectedTextAsync(CancellationToken cancellationToken = default) =>
        CopySelectedTextAsync(NativeMethods.GetForegroundWindow(), cancellationToken);

    public async Task<string?> CopySelectedTextAsync(nint targetWindow, CancellationToken cancellationToken = default)
    {
        await WaitForShortcutKeysReleasedAsync(cancellationToken);
        var clipboardSequence = NativeMethods.GetClipboardSequenceNumber();
        if (TrySendCopyMessage(targetWindow) && await WaitForClipboardChangeAsync(clipboardSequence, cancellationToken))
        {
            return await ReadClipboardTextAsync(cancellationToken);
        }

        var sentCopyInput = false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            sentCopyInput |= TrySendCopyShortcut();
            if (await WaitForClipboardChangeAsync(clipboardSequence, cancellationToken))
            {
                return await ReadClipboardTextAsync(cancellationToken);
            }

            await Task.Delay(60, cancellationToken);
        }

        if (TrySendCopyMessage(targetWindow) && await WaitForClipboardChangeAsync(clipboardSequence, cancellationToken))
        {
            return await ReadClipboardTextAsync(cancellationToken);
        }

        if (sentCopyInput)
        {
            return null;
        }

        throw new InvalidOperationException("无法读取目标程序的选中文本。若目标程序以管理员身份运行，请也以管理员身份启动 AITranslator。");
    }

    private static async Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken)
    {
        COMException? lastException = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var content = global::Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (!content.Contains(StandardDataFormats.Text))
                {
                    return null;
                }

                var text = (await content.GetTextAsync()).Trim();
                return text.Length switch
                {
                    0 => null,
                    > 20_000 => text[..20_000],
                    _ => text
                };
            }
            catch (COMException exception)
            {
                lastException = exception;
                await Task.Delay(40, cancellationToken);
            }
        }

        throw new InvalidOperationException("剪贴板正被其他程序占用，请重试。", lastException);
    }

    private static async Task WaitForShortcutKeysReleasedAsync(CancellationToken cancellationToken)
    {
        var keys = new[]
        {
            NativeMethods.VkControl, NativeMethods.VkLeftControl, NativeMethods.VkRightControl,
            NativeMethods.VkShift, NativeMethods.VkLeftShift, NativeMethods.VkRightShift,
            NativeMethods.VkAlt, NativeMethods.VkLeftAlt, NativeMethods.VkRightAlt,
            NativeMethods.VkLeftWindows, NativeMethods.VkRightWindows
        };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (keys.All(key => (NativeMethods.GetAsyncKeyState((int)key) & 0x8000) == 0))
            {
                return;
            }

            await Task.Delay(15, cancellationToken);
        }
    }

    private static async Task<bool> WaitForClipboardChangeAsync(uint initialSequence, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (NativeMethods.GetClipboardSequenceNumber() != initialSequence)
            {
                return true;
            }

            await Task.Delay(20, cancellationToken);
        }

        return false;
    }

    private static bool TrySendCopyShortcut()
    {
        var inputs = new[]
        {
            CreateKeyInput(NativeMethods.VkControl, false),
            CreateKeyInput(NativeMethods.VkC, false),
            CreateKeyInput(NativeMethods.VkC, true),
            CreateKeyInput(NativeMethods.VkControl, true)
        };

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        if (sent == inputs.Length)
        {
            return true;
        }

        var releases = new[] { CreateKeyInput(NativeMethods.VkC, true), CreateKeyInput(NativeMethods.VkControl, true) };
        NativeMethods.SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeMethods.Input>());
        return false;
    }

    private static bool TrySendCopyMessage(nint targetWindow)
    {
        if (targetWindow == 0)
        {
            return false;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(targetWindow, out _);
        var information = new NativeMethods.GuiThreadInfo { Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (threadId == 0 || !NativeMethods.GetGUIThreadInfo(threadId, ref information) || information.FocusedWindow == 0)
        {
            return false;
        }

        return NativeMethods.SendMessageTimeout(information.FocusedWindow, NativeMethods.WmCopy, 0, 0, NativeMethods.SmtoAbortIfHung, 500, out _) !=
               0;
    }

    private static NativeMethods.Input CreateKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new NativeMethods.InputUnion
        {
            Keyboard = new NativeMethods.KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? NativeMethods.KeyEventKeyUp : 0
            }
        }
    };
}