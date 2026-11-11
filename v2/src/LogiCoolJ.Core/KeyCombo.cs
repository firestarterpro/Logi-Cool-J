namespace LogiCoolJ.Core;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>
/// A key step such as "Ctrl+Shift+T", parsed into modifiers plus a Logi SDK VirtualKeyCode name.
/// The names are the members of Loupedeck.VirtualKeyCode (PluginApi); the plugin turns them into
/// enum values by name at runtime, and a unit test checks every name against the real enum.
/// </summary>
public sealed record KeyCombo(KeyModifiers Modifiers, string VirtualKey, string Display)
{
    /// <summary>Every Loupedeck.VirtualKeyCode member we may produce (from PluginApi 6.1.4).</summary>
    public static readonly IReadOnlySet<string> VirtualKeyNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Break", "Back", "Tab", "Clear", "Return", "Pause", "Caps", "Escape", "Space", "PageUp", "PageDown", "End", "Home",
        "ArrowLeft", "ArrowUp", "ArrowRight", "ArrowDown", "PrintScreen", "Insert", "Delete", "Help",
        "Key0", "Key1", "Key2", "Key3", "Key4", "Key5", "Key6", "Key7", "Key8", "Key9",
        "KeyA", "KeyB", "KeyC", "KeyD", "KeyE", "KeyF", "KeyG", "KeyH", "KeyI", "KeyJ", "KeyK", "KeyL", "KeyM",
        "KeyN", "KeyO", "KeyP", "KeyQ", "KeyR", "KeyS", "KeyT", "KeyU", "KeyV", "KeyW", "KeyX", "KeyY", "KeyZ",
        "WindowsLeft", "WindowsRight", "Apps", "Sleep",
        "NumPad0", "NumPad1", "NumPad2", "NumPad3", "NumPad4", "NumPad5", "NumPad6", "NumPad7", "NumPad8", "NumPad9",
        "Multiply", "Add", "Separator", "Subtract", "Decimal", "Divide",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "F13", "F14", "F15", "F16", "F17", "F18", "F19", "F20", "F21", "F22", "F23", "F24",
        "NumLock", "ScrollLock", "Shift", "Control", "Alt",
        "VolumeMute", "VolumeDown", "VolumeUp", "MediaNextTrack", "MediaPrevTrack", "MediaStop", "MediaPlayPause",
        "Oem1", "Equals", "Comma", "Minus", "Period", "Oem2", "Oem3", "Oem4", "Oem5", "Oem6", "Oem7", "Oem102",
    };

    private static readonly Dictionary<string, KeyModifiers> ModifierWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = KeyModifiers.Ctrl, ["control"] = KeyModifiers.Ctrl, ["shift"] = KeyModifiers.Shift,
        ["alt"] = KeyModifiers.Alt, ["win"] = KeyModifiers.Win, ["windows"] = KeyModifiers.Win,
    };

    // Friendly names used in layouts -> VirtualKeyCode names. Exact VirtualKeyCode names also work.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = "Return", ["return"] = "Return", ["tab"] = "Tab", ["space"] = "Space",
        ["esc"] = "Escape", ["escape"] = "Escape", ["backspace"] = "Back", ["back"] = "Back",
        ["delete"] = "Delete", ["del"] = "Delete", ["insert"] = "Insert", ["ins"] = "Insert",
        ["home"] = "Home", ["end"] = "End", ["pageup"] = "PageUp", ["pgup"] = "PageUp",
        ["pagedown"] = "PageDown", ["pgdn"] = "PageDown",
        ["left"] = "ArrowLeft", ["right"] = "ArrowRight", ["up"] = "ArrowUp", ["down"] = "ArrowDown",
        ["printscreen"] = "PrintScreen", ["prtsc"] = "PrintScreen", ["pause"] = "Pause",
        ["capslock"] = "Caps", ["numlock"] = "NumLock", ["scrolllock"] = "ScrollLock",
        ["apps"] = "Apps", ["menu"] = "Apps",
        ["win"] = "WindowsLeft", ["windows"] = "WindowsLeft", ["ctrl"] = "Control", ["shift"] = "Shift", ["alt"] = "Alt",
        ["`"] = "Oem3", ["backquote"] = "Oem3", ["backtick"] = "Oem3", ["."] = "Period", [","] = "Comma",
        ["-"] = "Minus", ["="] = "Equals", [";"] = "Oem1", ["/"] = "Oem2", ["["] = "Oem4", ["\\"] = "Oem5",
        ["]"] = "Oem6", ["'"] = "Oem7",
        ["mute"] = "VolumeMute", ["mediaprevioustrack"] = "MediaPrevTrack", ["mediaprev"] = "MediaPrevTrack",
        ["medianext"] = "MediaNextTrack", ["mediaplay"] = "MediaPlayPause", ["playpause"] = "MediaPlayPause",
    };

    public string ToKeyString()
    {
        var parts = new List<string>();
        if (this.Modifiers.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (this.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (this.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (this.Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        parts.Add(this.Display);
        return string.Join("+", parts);
    }

    /// <summary>Parses "Ctrl+Shift+T". On failure returns null and a reason that never repeats long input.</summary>
    public static KeyCombo? TryParse(string? text, out string error)
    {
        error = "";
        var s = (text ?? "").Trim();
        if (s.Length == 0)
        {
            error = "the key is empty";
            return null;
        }

        var parts = s == "+" ? new List<string> { "+" } : s.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0)
        {
            error = "the key is empty";
            return null;
        }

        var mods = KeyModifiers.None;
        var bad = new List<string>();
        foreach (var p in parts.Take(parts.Count - 1))
        {
            if (ModifierWords.TryGetValue(p, out var m)) mods |= m;
            else bad.Add(p);
        }

        if (bad.Count > 0)
        {
            error = $"unknown modifier '{string.Join("+", bad)}' (use Ctrl, Shift, Alt, Win)";
            return null;
        }

        var key = parts[^1];
        var vk = KeyName(key);
        if (vk is null)
        {
            error = $"unknown key name '{(key.Length > 24 ? key[..24] + "…" : key)}'";
            return null;
        }

        var display = key.Length == 1 ? key.ToUpperInvariant() : key;
        return new KeyCombo(mods, vk, display);
    }

    /// <summary>VirtualKeyCode name for one key token, or null.</summary>
    public static string? KeyName(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetter(key[0])) return "Key" + char.ToUpperInvariant(key[0]);
        if (key.Length == 1 && char.IsAsciiDigit(key[0])) return "Key" + key;
        if (Aliases.TryGetValue(key, out var alias)) return alias;
        return VirtualKeyNames.FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
    }
}
