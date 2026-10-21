using ContexWin.App;

var root = Path.Combine(Path.GetTempPath(), "ContexWin-checks-" + Guid.NewGuid().ToString("N"));
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
    File.AppendAllText(path, " ");
    Check(!store.Apply(store.CreateDraft()).Success, "external edit blocked");
    File.WriteAllText(path, "{");
    Check(!store.Reload().Success, "invalid reload blocked");
    Check(store.Current.Items.Count == 2, "live config preserved");
    Console.WriteLine("All configuration checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
