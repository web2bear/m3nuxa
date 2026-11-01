using System.Text.Json;

namespace m3nuxa.App;

public sealed record AppSettings
{
    public string MainHotkey { get; init; } = "Ctrl+Alt+Space";
    public double MenuFontSize { get; init; } = 12;
    public string Language { get; init; } = "en-US";
}

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
            Current = Current with { Language = NormalizeLanguage(Current.Language) };
            Validate(Current, this.localize);
        }
        else { Current = new AppSettings { MainHotkey = legacyHotkey }; Save(Current); }
    }
    public static string NormalizeLanguage(string? language) =>
        string.Equals(language, "ru-RU", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "en-US";
    private static string DefaultMessage(string key) => key switch
    {
        "Settings/EmptyFile" => "The settings file is empty.",
        "Settings/InvalidShortcut" => "The menu shortcut is invalid.",
        "Settings/InvalidFontSize" => "The font size must be between 10 and 24.",
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };
    public static void Validate(AppSettings value, Func<string, string>? localize = null)
    {
        localize ??= DefaultMessage;
        if (value.MainHotkey is null || (value.MainHotkey.Length > 0 && !HotkeyParser.TryParse(value.MainHotkey, out _, out _)))
            throw new InvalidDataException(localize("Settings/InvalidShortcut"));
        if (!double.IsFinite(value.MenuFontSize) || value.MenuFontSize < 10 || value.MenuFontSize > 24)
            throw new InvalidDataException(localize("Settings/InvalidFontSize"));
    }
    public void Save(AppSettings value)
    {
        value = value with { Language = NormalizeLanguage(value.Language) };
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
