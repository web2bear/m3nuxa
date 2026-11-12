using System.Globalization;
using System.Text.Json;
using Microsoft.Windows.ApplicationModel.Resources;

namespace m3nuxa.App;

/// <summary>String resources for the language selected for this process.</summary>
public static class L
{
    public const string DefaultLanguage = "en-US";
    public static string Language { get; private set; } = ReadLanguage();
    private static readonly object Sync = new();

    private static readonly Lazy<(ResourceManager Manager, ResourceContext Context)> Resources = new(() =>
    {
        // Both executables share a portable directory but have separate PRI files.
        var name = Path.GetFileNameWithoutExtension(Environment.ProcessPath)
            ?? throw new InvalidOperationException("The executable path is unavailable.");
        var manager = new ResourceManager(Path.Combine(AppContext.BaseDirectory, name + ".pri"));
        var context = manager.CreateResourceContext();
        context.QualifierValues["Language"] = Language;
        return (manager, context);
    });

    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var separator = key.IndexOf('/');
        var map = separator < 0 ? "Resources" : key[..separator];
        var name = separator < 0 ? key : key[(separator + 1)..];
        lock (Sync)
        {
            var (manager, context) = Resources.Value;
            return manager.MainResourceMap.GetSubtree(map).GetValue(name, context).ValueAsString;
        }
    }

    public static void SetLanguage(string language)
    {
        var normalized = string.Equals(language, "ru-RU", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : DefaultLanguage;
        lock (Sync)
        {
            Resources.Value.Context.QualifierValues["Language"] = normalized;
            Language = normalized;
        }
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    private static string LanguagePath => Path.Combine(AppContext.BaseDirectory, "app-language.json");

    private static string ReadLanguage()
    {
        try { return ReadLanguageFile(LanguagePath, false) ?? DefaultLanguage; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not read UI language: {ex.Message}");
            return DefaultLanguage;
        }
    }

    private static string? ReadLanguageFile(string path, bool configuration)
    {
        if (!File.Exists(path)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (configuration && (!root.TryGetProperty("Settings", out root) || root.ValueKind != JsonValueKind.Object)) return null;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Language", out var language) && language.ValueKind == JsonValueKind.String
            ? NormalizeLanguage(language.GetString()) : null;
    }

    private static string NormalizeLanguage(string? language) =>
        string.Equals(language, "ru-RU", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : DefaultLanguage;

    public static void MigrateLanguage(string configurationPath, string legacySettingsPath)
    {
        if (File.Exists(LanguagePath)) return;
        try
        {
            var language = ReadLanguageFile(configurationPath, true) ?? ReadLanguageFile(legacySettingsPath, false);
            SaveLanguage(language ?? DefaultLanguage);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not migrate UI language: {ex.Message}");
        }
    }

    public static void SaveLanguage(string language)
    {
        var normalized = NormalizeLanguage(language);
        var temporary = LanguagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { Language = normalized }));
            File.Move(temporary, LanguagePath, true);
            SetLanguage(normalized);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
