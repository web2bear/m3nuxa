using System.Text.Json;
using m3nuxa.App;

namespace m3nuxa.App.Configuration;

public sealed record GlobalSettings
{
    public string Language { get; init; } = L.DefaultLanguage;
    public Dictionary<string, string> ActiveProfiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public bool McpHttpEnabled { get; init; }
    public int? PanelX { get; init; }
    public int? PanelY { get; init; }
    public double? PanelWidth { get; init; }
    public double? PanelHeight { get; init; }
}

public sealed class GlobalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private readonly string path;
    private GlobalSettings current;

    public GlobalSettingsStore()
    {
        path = Path.Combine(AppContext.BaseDirectory, "m3nuxa.App.config");
        var exists = File.Exists(path);
        current = exists ? Load(path) : new GlobalSettings();
        current = current with
        {
            Language = string.Equals(current.Language, "ru-RU", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : L.DefaultLanguage,
            ActiveProfiles = current.ActiveProfiles is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(current.ActiveProfiles, StringComparer.OrdinalIgnoreCase)
        };
        if (!exists) Save(current);
    }

    public GlobalSettings Current { get { lock (gate) return current with { }; } }

    public string? GetActiveProfile(string configDirectory)
    {
        var key = NormalizeConfigDirectory(configDirectory);
        lock (gate) return current.ActiveProfiles.TryGetValue(key, out var profile) ? profile : null;
    }

    public void SetActiveProfile(string configDirectory, string profile)
    {
        var key = NormalizeConfigDirectory(configDirectory);
        Update(settings =>
        {
            var profiles = new Dictionary<string, string>(settings.ActiveProfiles, StringComparer.OrdinalIgnoreCase)
            {
                [key] = profile
            };
            return settings with { ActiveProfiles = profiles };
        });
    }

    private static string NormalizeConfigDirectory(string configDirectory)
    {
        var fullPath = Path.GetFullPath(configDirectory);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public void Update(Func<GlobalSettings, GlobalSettings> update)
    {
        lock (gate)
        {
            var candidate = update(current);
            Save(candidate);
            current = candidate;
        }
    }

    private void Save(GlobalSettings settings)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static GlobalSettings Load(string settingsPath)
    {
        return JsonSerializer.Deserialize<GlobalSettings>(File.ReadAllText(settingsPath), JsonOptions) ?? new GlobalSettings();
    }
}
