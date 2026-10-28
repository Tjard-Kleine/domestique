using System.Windows.Input;

namespace Domestique.App;

// Global shortcuts that work in every app (e.g. over a video). The value doubles as the RegisterHotKey id.
public enum HotkeyAction { ErgUp = 1, ErgDown, ErgToggle, ClickThrough, Compact, Map, Settings }

// Modifiers plus one key, stored as readable text such as "Ctrl+Alt+PageUp".
public readonly record struct KeyCombo(ModifierKeys Modifiers, Key Key)
{
    private static readonly (ModifierKeys Flag, string Name)[] Parts =
        [(ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win")];

    public static KeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        var tokens = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens[..^1])
        {
            int i = Array.FindIndex(Parts, p => p.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            mods |= Parts[i].Flag;
        }
        return Enum.TryParse(tokens[^1], true, out Key key) && key != Key.None ? new KeyCombo(mods, key) : null;
    }

    public override string ToString() => string.Join("+", Names(Modifiers).Append(Key.ToString()));

    // For menus, tooltips and settings: "Ctrl+Alt+PgUp".
    public string Display => string.Join("+", Names(Modifiers).Append(KeyName(Key)));

    // Only the modifiers, e.g. "Ctrl+Alt" while a shortcut is being pressed.
    public static string ModifierText(ModifierKeys mods) => string.Join("+", Names(mods));

    private static IEnumerable<string> Names(ModifierKeys mods) => Parts.Where(p => mods.HasFlag(p.Flag)).Select(p => p.Name);

    public uint NativeModifiers =>
        (Modifiers.HasFlag(ModifierKeys.Alt) ? 0x1u : 0) | (Modifiers.HasFlag(ModifierKeys.Control) ? 0x2u : 0)
        | (Modifiers.HasFlag(ModifierKeys.Shift) ? 0x4u : 0) | (Modifiers.HasFlag(ModifierKeys.Windows) ? 0x8u : 0);

    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public static string KeyName(Key key) => key switch
    {
        Key.PageUp => "PgUp", Key.PageDown => "PgDn", Key.Space => "Space", Key.Return => "Enter", Key.Escape => "Esc",
        Key.Up => "↑", Key.Down => "↓", Key.Left => "←", Key.Right => "→",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (key - Key.NumPad0),
        Key.Add => "Num +", Key.Subtract => "Num −", Key.OemPlus => "+", Key.OemMinus => "−",
        _ => key.ToString(),
    };
}

// Actions, labels and default shortcuts. Users can change them in the settings.
public static class Hotkeys
{
    // Defaults use Ctrl+Alt with the initial letter. Avoided on purpose: Ctrl+Alt+arrows (rotate the screen on some
    // graphics drivers, multi-cursor in VS Code) and AltGr characters of German keyboards (@ € µ { [ ] } \ ~ |).
    public static readonly (HotkeyAction Action, string Label, KeyCombo Default)[] All =
    [
        (HotkeyAction.ErgUp, "ERG +10 W", new(ModifierKeys.Control | ModifierKeys.Alt, Key.PageUp)),
        (HotkeyAction.ErgDown, "ERG −10 W", new(ModifierKeys.Control | ModifierKeys.Alt, Key.PageDown)),
        (HotkeyAction.ErgToggle, "ERG on / off", new(ModifierKeys.Control | ModifierKeys.Alt, Key.Space)),
        (HotkeyAction.ClickThrough, "Click-through on / off", new(ModifierKeys.Control | ModifierKeys.Alt, Key.C)),
        (HotkeyAction.Compact, "Small notch on / off", new(ModifierKeys.Control | ModifierKeys.Alt, Key.N)),
        (HotkeyAction.Map, "Map on / off", new(ModifierKeys.Control | ModifierKeys.Alt, Key.R)),
        (HotkeyAction.Settings, "Settings", new(ModifierKeys.Control | ModifierKeys.Alt, Key.S)),
    ];

    // No entry means the default; an empty entry means no shortcut.
    public static KeyCombo? Get(AppSettings s, HotkeyAction action) =>
        s.Hotkeys.TryGetValue(action.ToString(), out var text) ? KeyCombo.Parse(text) : All.First(h => h.Action == action).Default;

    public static void Set(AppSettings s, HotkeyAction action, KeyCombo? combo) => s.Hotkeys[action.ToString()] = combo?.ToString() ?? "";

    public static string Label(HotkeyAction action) => All.First(h => h.Action == action).Label;

    // Only ERG ± repeat while held; toggles must not flicker (MOD_NOREPEAT).
    public static uint Repeat(HotkeyAction action) => action is HotkeyAction.ErgUp or HotkeyAction.ErgDown ? 0 : 0x4000u;

    // "Ctrl+Alt+C", or null without a shortcut.
    public static string? Text(AppSettings s, HotkeyAction action) => Get(s, action)?.Display;
}
