using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace m3nuxa.App;

/// <summary>String resources for the language selected for this process.</summary>
public static class L
{
    public const string DefaultLanguage = "en-US";
    public static string Language { get; private set; } = ReadArgumentLanguage();
    private static readonly object Sync = new();

    private static string ReadArgumentLanguage()
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, "--language");
        return index >= 0 && index + 1 < args.Length && args[index + 1].Equals("ru-RU", StringComparison.OrdinalIgnoreCase)
            ? "ru-RU" : DefaultLanguage;
    }

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

}
