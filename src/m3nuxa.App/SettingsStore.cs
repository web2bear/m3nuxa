using System.Text.Json;

namespace m3nuxa.App;

public sealed record AppSettings
{
    public string MainHotkey { get; init; } = "Ctrl+Alt+Space";
    public double MenuFontSize { get; init; } = 12;
}

public sealed class SettingsStore
{
    private readonly string path;
    public AppSettings Current { get; private set; }
    public SettingsStore(string path, string legacyHotkey)
    {
        this.path = path;
        if (File.Exists(path))
        {
            Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Файл настроек пуст.");
            Validate(Current);
        }
        else { Current = new AppSettings { MainHotkey = legacyHotkey }; Save(Current); }
    }
    public static void Validate(AppSettings value)
    {
        if (value.MainHotkey is null || (value.MainHotkey.Length > 0 && !HotkeyParser.TryParse(value.MainHotkey, out _, out _)))
            throw new InvalidDataException("Некорректное сочетание вызова меню.");
        if (!double.IsFinite(value.MenuFontSize) || value.MenuFontSize < 10 || value.MenuFontSize > 24)
            throw new InvalidDataException("Размер шрифта должен быть от 10 до 24.");
    }
    public void Save(AppSettings value)
    {
        Validate(value);
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
