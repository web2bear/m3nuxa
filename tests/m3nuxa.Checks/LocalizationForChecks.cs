using System.Globalization;
using System.Xml.Linq;

namespace m3nuxa.App;

// The non-WinUI checks exercise domain code without starting the Windows App SDK.
// They read the actual English source resources; PRI loading is checked separately.
public static class L
{
    public const string DefaultLanguage = "en-US";
    public static string Language => DefaultLanguage;
    public static string Get(string key)
    {
        var separator = key.IndexOf('/');
        var map = separator < 0 ? "Resources" : key[..separator];
        var name = separator < 0 ? key : key[(separator + 1)..];
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"Test.{map}.resw")
            ?? throw new InvalidOperationException($"Missing resource map: {map}");
        var document = XDocument.Load(stream);
        return document.Root!.Elements("data").Single(x => (string?)x.Attribute("name") == name).Element("value")!.Value;
    }
    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
