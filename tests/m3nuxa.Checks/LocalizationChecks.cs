using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class LocalizationChecks
{
    public static void Run()
    {
        CheckPair("App resources", "Test.Resources.resw", "Localization.App.ru-RU.Resources.resw");
        CheckPair("App settings", "Test.Settings.resw", "Localization.App.ru-RU.Settings.resw");
        CheckPair("Editor resources", "Localization.Editor.en-US.Resources.resw", "Localization.Editor.ru-RU.Resources.resw");
        Console.WriteLine("Localization key and format checks passed.");
    }

    private static void CheckPair(string name, string englishName, string russianName)
    {
        var english = Read(englishName);
        var russian = Read(russianName);
        var onlyEnglish = english.Keys.Except(russian.Keys).Order().ToArray();
        var onlyRussian = russian.Keys.Except(english.Keys).Order().ToArray();
        if (onlyEnglish.Length != 0 || onlyRussian.Length != 0)
            throw new InvalidDataException($"{name}: missing Russian keys: {string.Join(", ", onlyEnglish)}; missing English keys: {string.Join(", ", onlyRussian)}");
        foreach (var (key, value) in english)
        {
            var en = Placeholders(value, name, key);
            var ru = Placeholders(russian[key], name, key);
            if (!en.SetEquals(ru))
                throw new InvalidDataException($"{name}/{key}: format argument indexes differ: English [{string.Join(", ", en.Order())}], Russian [{string.Join(", ", ru.Order())}]");
        }
    }

    private static Dictionary<string, string> Read(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"Missing embedded localization resource {resource}.");
        return XDocument.Load(stream).Root!.Elements("data").ToDictionary(
            data => (string?)data.Attribute("name") ?? throw new InvalidDataException($"Unnamed resource in {resource}."),
            data => (string?)data.Element("value") ?? throw new InvalidDataException($"Resource value is missing in {resource}."),
            StringComparer.Ordinal);
    }

    private static HashSet<int> Placeholders(string value, string name, string key)
    {
        // Resource values may also contain literal template variables such as {selectedPaths}.
        var matches = Regex.Matches(value, @"(?<!\{)\{\s*(\d+)(?:\s*,\s*-?\d+)?(?:\s*:[^{}]*)?\}(?!\})");
        if (Regex.IsMatch(value, @"(?<!\{)\{\s*\d"))
        {
            try { _ = CompositeFormat.Parse(value); }
            catch (FormatException ex) { throw new InvalidDataException($"{name}/{key}: invalid format string.", ex); }
        }
        return matches.Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
    }
}
