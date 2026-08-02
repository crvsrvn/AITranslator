using AITranslator.Interop;

namespace AITranslator.Services;

public enum HotkeyGestureKind
{
    RegisterHotKey,
    DoubleControl
}

public sealed record HotkeyGesture(HotkeyGestureKind Kind, uint Modifiers, uint VirtualKey, string DisplayText)
{
    public const string DoubleControlDisplayText = "双击 Ctrl";

    public static bool TryParse(string value, out HotkeyGesture? gesture, out string? error)
    {
        gesture = null;
        error = null;
        if (string.Equals(value.Trim(), DoubleControlDisplayText, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value.Trim(), "DoubleCtrl", StringComparison.OrdinalIgnoreCase))
        {
            gesture = new HotkeyGesture(HotkeyGestureKind.DoubleControl, 0, 0, DoubleControlDisplayText);
            return true;
        }

        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "快捷键不能为空。";
            return false;
        }

        uint modifiers = NativeMethods.ModNoRepeat;
        var displayModifiers = new List<string>();
        uint? virtualKey = null;
        string? displayKey = null;

        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.ModControl;
                    AddOnce(displayModifiers, "Ctrl");
                    continue;
                case "ALT":
                    modifiers |= NativeMethods.ModAlt;
                    AddOnce(displayModifiers, "Alt");
                    continue;
                case "SHIFT":
                    modifiers |= NativeMethods.ModShift;
                    AddOnce(displayModifiers, "Shift");
                    continue;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeMethods.ModWin;
                    AddOnce(displayModifiers, "Win");
                    continue;
            }

            if (virtualKey is not null || !TryParseKey(part, out var parsedKey, out var parsedDisplay))
            {
                error = $"无法识别快捷键“{value}”。";
                return false;
            }

            virtualKey = parsedKey;
            displayKey = parsedDisplay;
        }

        if (virtualKey is null)
        {
            error = "快捷键必须包含一个普通按键。";
            return false;
        }

        if (virtualKey == NativeMethods.VkF12)
        {
            error = "F12 由 Windows 保留，不能注册为全局快捷键。";
            return false;
        }

        if (displayModifiers.Count == 0 && !IsFunctionKey(virtualKey.Value))
        {
            error = "不带修饰键时仅支持 F1-F11、F13-F24。";
            return false;
        }

        var display = string.Join('+', displayModifiers.Append(displayKey!));
        gesture = new HotkeyGesture(HotkeyGestureKind.RegisterHotKey, modifiers, virtualKey.Value, display);
        return true;
    }

    public static bool TryCreateFromKey(int virtualKey, bool control, bool alt, bool shift, bool windows, out string shortcut)
    {
        shortcut = string.Empty;
        var key = (uint)virtualKey;
        if (key == NativeMethods.VkF12 || !TryFormatKey(key, out var keyText))
        {
            return false;
        }

        if (!control && !alt && !shift && !windows && !IsFunctionKey(key))
        {
            return false;
        }

        var parts = new List<string>();
        if (control)
        {
            parts.Add("Ctrl");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        if (windows)
        {
            parts.Add("Win");
        }

        parts.Add(keyText);
        shortcut = string.Join('+', parts);
        return true;
    }

    private static bool TryParseKey(string value, out uint virtualKey, out string display)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            virtualKey = normalized[0];
            display = normalized;
            return true;
        }

        if (normalized.StartsWith('F') && int.TryParse(normalized[1..], out var functionNumber) && functionNumber is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionNumber - 1);
            display = $"F{functionNumber}";
            return true;
        }

        (virtualKey, display) = normalized switch
        {
            "SPACE" => (0x20u, "Space"),
            "ENTER" => (0x0Du, "Enter"),
            "TAB" => (0x09u, "Tab"),
            "HOME" => (0x24u, "Home"),
            "END" => (0x23u, "End"),
            "PAGEUP" => (0x21u, "PageUp"),
            "PAGEDOWN" => (0x22u, "PageDown"),
            "UP" => (0x26u, "Up"),
            "DOWN" => (0x28u, "Down"),
            "LEFT" => (0x25u, "Left"),
            "RIGHT" => (0x27u, "Right"),
            _ => (0u, string.Empty)
        };
        return virtualKey != 0;
    }

    private static bool TryFormatKey(uint virtualKey, out string display)
    {
        if (virtualKey is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39)
        {
            display = ((char)virtualKey).ToString();
            return true;
        }

        if (IsFunctionKey(virtualKey))
        {
            display = $"F{virtualKey - 0x70 + 1}";
            return true;
        }

        display = virtualKey switch
        {
            0x20 => "Space",
            0x0D => "Enter",
            0x09 => "Tab",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x26 => "Up",
            0x28 => "Down",
            0x25 => "Left",
            0x27 => "Right",
            _ => string.Empty
        };
        return display.Length > 0;
    }

    private static bool IsFunctionKey(uint virtualKey) => virtualKey is >= 0x70 and <= 0x87;

    private static void AddOnce(ICollection<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.Ordinal))
        {
            values.Add(value);
        }
    }
}
