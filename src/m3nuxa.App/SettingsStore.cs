using System.Text.Json;

namespace m3nuxa.App;

public sealed class SettingsStore
{
    private readonly string path;
    private readonly Func<string, string> localize;
    public AppSettings Current { get; private set; }
    public SettingsStore(string path, string legacyHotkey, Func<string, string>? localize = null)
    {
        this.path = path;
        this.localize = localize ?? DefaultMessage;
        if (File.Exists(path))
        {
            Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                ?? throw new InvalidDataException(this.localize("Settings/EmptyFile"));
            Validate(Current, this.localize);
        }
        else { Current = new AppSettings { MainHotkey = legacyHotkey }; Save(Current); }
    }
    private static string DefaultMessage(string key) => key switch
    {
        "Settings/EmptyFile" => "The settings file is empty.",
        "Settings/InvalidShortcut" => "The menu shortcut is invalid.",
        "Settings/InvalidPanelShortcut" => "The panel shortcut is invalid.",
        "Settings/InvalidPanelPosition" => "The panel position is invalid.",
        "Settings/InvalidFontSize" => "The font size must be between 10 and 24.",
        "Settings/InvalidAppearance" => "The appearance settings are invalid.",
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
    public static void Validate(AppSettings value, Func<string, string>? localize = null)
    {
        localize ??= DefaultMessage;
        if (value.MainHotkey is null || (value.MainHotkey.Length > 0 && !HotkeyParser.TryParse(value.MainHotkey, out _, out _)))
            throw new InvalidDataException(localize("Settings/InvalidShortcut"));
        if (value.PanelHotkey is null || (value.PanelHotkey.Length > 0 && !HotkeyParser.TryParse(value.PanelHotkey, out _, out _)))
            throw new InvalidDataException(localize("Settings/InvalidPanelShortcut"));
        if (value.PanelPosition is not ("Center" or "Cursor"))
            throw new InvalidDataException(localize("Settings/InvalidPanelPosition"));
        if (!double.IsFinite(value.MenuFontSize) || value.MenuFontSize < 10 || value.MenuFontSize > 24)
            throw new InvalidDataException(localize("Settings/InvalidFontSize"));
        static bool ColorValid(string? color) => color is not null && (color.Length == 0 ||
            color.Length == 7 && color[0] == '#' && uint.TryParse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _));
        if (value.MenuDensity is not ("Compact" or "Normal" or "Comfortable") ||
            value.MenuIconSize is not ("Small" or "Normal" or "Large") ||
            value.PanelIconSize is not ("Small" or "Normal" or "Large") ||
            !ColorValid(value.MenuBackground) || !ColorValid(value.PanelBackground) ||
            value.PanelOpacity is < 40 or > 100 || !double.IsFinite(value.PanelFontSize) || value.PanelFontSize is < 10 or > 24)
            throw new InvalidDataException(localize("Settings/InvalidAppearance"));
    }
    public void Save(AppSettings value)
    {
        Validate(value, localize);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
            Current = value;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
