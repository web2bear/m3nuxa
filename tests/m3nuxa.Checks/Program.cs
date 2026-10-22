using m3nuxa.App;
using System.Reflection;

var root = Path.Combine(Path.GetTempPath(), "m3nuxa-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var path = Path.Combine(root, "config.json");
    var store = new ConfigStore(path);
    var first = store.CreateDraft();
    first.Config.Items.Add(new MenuNode { Name = "Work", Kind = MenuNodeKind.Group });
    Check(store.Apply(first).Success, "first apply");
    var stale = store.CreateDraft();
    var next = store.CreateDraft();
    next.Config.Items.Add(new MenuNode { Name = "Other", Kind = MenuNodeKind.Group });
    Check(store.Apply(next).Success, "second apply");
    Check(!store.Apply(stale).Success, "stale draft blocked");
    Check(Directory.GetFiles(Path.Combine(root, "backups"), "*.json").Length == 1, "backup created");
    var invalid = store.CreateDraft();
    invalid.Config.Items.Add(new MenuNode { Kind = MenuNodeKind.Separator, Hotkey = "Ctrl+Alt+X" });
    Check(!store.Apply(invalid).Success, "invalid separator blocked");
    var duplicate = store.CreateDraft();
    duplicate.Config.Items.Add(new MenuNode { Name = "First", Kind = MenuNodeKind.Group, Hotkey = "Ctrl+Shift+K" });
    duplicate.Config.Items.Add(new MenuNode { Name = "Second", Kind = MenuNodeKind.Group, Hotkey = "Control+Shift+K" });
    Check(!store.Apply(duplicate).Success, "equivalent hotkeys blocked");
    var malformed = store.CreateDraft();
    malformed.Config.Items.Add(new MenuNode { Name = "Broken", Kind = MenuNodeKind.Group, Hotkey = "Ctrl+" });
    Check(!store.Apply(malformed).Success, "malformed hotkey blocked");
    var snippet = store.CreateDraft();
    snippet.Config.Items.Add(new MenuNode { Name = "Snippet", Kind = MenuNodeKind.Action,
        Action = new MenuAction { Kind = ActionKind.InsertText, Text = "Привет" } });
    Check(store.Validate(snippet.Config).IsValid, "text snippet valid");
    File.AppendAllText(path, " ");
    Check(!store.Apply(store.CreateDraft()).Success, "external edit blocked");
    File.WriteAllText(path, "{");
    Check(!store.Reload().Success, "invalid reload blocked");
    Check(store.Current.Items.Count == 2, "live config preserved");
    using (var folderIcon = ItemIcon.Load(new MenuNode { Action = new MenuAction { Path = Environment.GetFolderPath(Environment.SpecialFolder.Windows) } }))
        Check(folderIcon is { Width: > 0 }, "Explorer folder icon");
    using (var programIcon = ItemIcon.Load(new MenuNode { IconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") }))
        Check(programIcon is { Width: > 0 }, "executable shell icon, not Image.FromFile");
    var customIcon = Path.Combine(root, "custom.png");
    using (var bitmap = new System.Drawing.Bitmap(16, 16))
    { bitmap.SetPixel(0, 0, System.Drawing.Color.Magenta); bitmap.Save(customIcon); }
    var iconNode = new MenuNode { IconPath = customIcon, Action = new MenuAction { Path = root } };
    using (var loaded = ItemIcon.Load(iconNode))
        Check(loaded?.GetPixel(0, 0).ToArgb() == System.Drawing.Color.Magenta.ToArgb(), "custom image overrides shell icon");
    File.WriteAllText(customIcon, "broken image");
    using (var fallback = ItemIcon.Load(iconNode))
        Check(fallback is { Width: > 0 }, "corrupt custom icon falls back to Explorer");
    Check(ItemIcon.Png(new MenuNode { Action = new MenuAction { Path = root } }) is { Length: > 0 }, "editor PNG shell icon");
    Check(HotkeyParser.TryParse("Ctrl+Alt+Space", out var modifiers, out var key) && modifiers == 3 && key == 32, "global hotkey parse");
    Check(HotkeyParser.TryParse("Win+D1", out modifiers, out key) && modifiers == 8 && key == 49, "legacy digit key alias");
    Check(HotkeyParser.TryParse("Ctrl+OemPlus", out modifiers, out key) && key == 0xbb, "legacy OEM key alias");
    Check(!HotkeyParser.TryParse("Ctrl+Ctrl+A", out _, out _), "duplicate modifiers rejected");
    Check(!HotkeyParser.TryParse("Ctrl+", out _, out _), "missing key rejected");
    Console.WriteLine("All configuration and icon checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
