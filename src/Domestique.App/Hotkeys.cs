using System.Windows.Input;

namespace Domestique.App;

// Globale Tastenkürzel: wirken in jeder App (z. B. über dem Video). Jede Aktion hat ein Standardkürzel und lässt sich in
// den Einstellungen ändern. Die Zahl ist zugleich die Kennung bei RegisterHotKey.
public enum HotkeyAction { ErgUp = 1, ErgDown, ErgToggle, ClickThrough, Compact, Map, Settings }

// Tastenkombination aus Umschalttasten und einer Taste; gespeichert als lesbarer Text, z. B. „Ctrl+Alt+PageUp“
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

    // für Menü, Tooltips und Einstellungen: „Ctrl+Alt+PgUp“
    public string Display => string.Join("+", Names(Modifiers).Append(KeyName(Key)));

    // nur die Umschalttasten, z. B. „Ctrl+Alt“ (während man ein Kürzel drückt)
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

public static class Hotkeys
{
    // Standard: alles mit Strg+Alt, Buchstaben nach dem Anfangsbuchstaben. Bewusst nicht belegt: Strg+Alt+Pfeile (drehen bei
    // manchen Grafiktreibern den Bildschirm, in VS Code mehrere Cursor) und AltGr-Zeichen deutscher Tastaturen (@ € µ { [ ] } \ ~ |).
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

    // ohne Eintrag gilt der Standard; ein leerer Eintrag heißt „kein Kürzel“
    public static KeyCombo? Get(AppSettings s, HotkeyAction action) =>
        s.Hotkeys.TryGetValue(action.ToString(), out var text) ? KeyCombo.Parse(text) : All.First(h => h.Action == action).Default;

    public static void Set(AppSettings s, HotkeyAction action, KeyCombo? combo) => s.Hotkeys[action.ToString()] = combo?.ToString() ?? "";

    public static string Label(HotkeyAction action) => All.First(h => h.Action == action).Label;

    // Halten wiederholt nur ERG ±; Umschalten soll beim Halten nicht flackern (MOD_NOREPEAT)
    public static uint Repeat(HotkeyAction action) => action is HotkeyAction.ErgUp or HotkeyAction.ErgDown ? 0 : 0x4000u;

    // für Texte: „Ctrl+Alt+C“ oder null ohne Kürzel
    public static string? Text(AppSettings s, HotkeyAction action) => Get(s, action)?.Display;
}
