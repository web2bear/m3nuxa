using m3nuxa.App;
using System.Reflection;

var root = Path.Combine(Path.GetTempPath(), "m3nuxa-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    WorkflowModelChecks.Run(root);
    WorkflowExecutionChecks.Run(root);
    AgentPipeWorkflowChecks.Run(root);
    LocalizationChecks.Run();
    EditorDraftChecks.Run();
    var path = Path.Combine(root, "config.json");
    var store = new ConfigStore(path);
    var legacyPath = Path.Combine(root, "legacy.json");
    File.WriteAllText(legacyPath, "{\"MainHotkey\":\"Ctrl+Shift+F8\",\"Items\":[]}");
    Check(new ConfigStore(legacyPath).Current.MainHotkey == "Ctrl+Shift+F8", "legacy shortcut read from existing JSON");
    var settingsPath = Path.Combine(root, "settings.json");
    var settings = new SettingsStore(settingsPath, "Ctrl+Shift+F8");
    Check(settings.Current.MainHotkey == "Ctrl+Shift+F8", "legacy global shortcut migrated");
    Check(settings.Current.Language == "en-US", "default language is English");
    File.WriteAllText(settingsPath, "{\"MainHotkey\":\"Ctrl+Shift+F8\",\"MenuFontSize\":12}");
    Check(new SettingsStore(settingsPath, "").Current.Language == "en-US", "legacy settings default to English");
    File.WriteAllText(settingsPath, "{\"Language\":\"unknown\"}");
    Check(new SettingsStore(settingsPath, "").Current.Language == "en-US", "unsupported language falls back to English");
    settings.Save(settings.Current with { MainHotkey = "Ctrl+Alt+F9", MenuFontSize = 16, Language = "ru-RU" });
    Check(new SettingsStore(settingsPath, "Ctrl+Shift+F8").Current == settings.Current, "settings survive restart and migration does not overwrite");
    store.GlobalHotkey = () => settings.Current.MainHotkey;
    var settingsDraft = store.CreateDraft();
    settingsDraft.Config.MainHotkey = "Ctrl+Alt+F1";
    Check(store.Apply(settingsDraft).Success && settings.Current.MainHotkey == "Ctrl+Alt+F9", "menu draft cannot overwrite global settings");
    Check(!File.ReadAllText(path).Contains("MainHotkey"), "new menu file excludes global shortcut");
    var conflictDraft = store.CreateDraft();
    conflictDraft.Config.Items.Add(new MenuNode { Name = "Conflict", Kind = MenuNodeKind.Group, Hotkey = "Control+Alt+F9" });
    Check(!store.Validate(conflictDraft.Config).IsValid, "item shortcut validated against current settings");
    try { settings.Save(settings.Current with { MenuFontSize = double.NaN }); throw new Exception("invalid font accepted"); }
    catch (InvalidDataException) { Check(settings.Current.MenuFontSize == 16, "invalid settings preserve current state"); }
    File.Delete(path);
    store = new ConfigStore(path);
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
    var largeIcon = Path.Combine(root, "large-photo.jpg");
    using (var photo = new System.Drawing.Bitmap(4000, 3000))
    using (var graphics = System.Drawing.Graphics.FromImage(photo))
    { graphics.Clear(System.Drawing.Color.MediumPurple); photo.Save(largeIcon, System.Drawing.Imaging.ImageFormat.Jpeg); }
    var largeNode = new MenuNode { IconPath = largeIcon };
    var iconWatch = System.Diagnostics.Stopwatch.StartNew();
    var iconPng = ItemIcon.PngAsync(largeNode).GetAwaiter().GetResult()!;
    iconWatch.Stop();
    using (var iconStream = new MemoryStream(iconPng))
    using (var thumbnail = System.Drawing.Image.FromStream(iconStream))
    {
        Console.WriteLine($"Large image icon: {thumbnail.Width}x{thumbnail.Height}, {iconPng.Length} bytes, {iconWatch.ElapsedMilliseconds} ms");
        Check(thumbnail.Width == 64 && thumbnail.Height == 48, "large source produces bounded icon without distortion");
    }
    iconWatch.Restart();
    var warmIconPng = ItemIcon.PngAsync(largeNode).GetAwaiter().GetResult()!;
    iconWatch.Stop();
    Console.WriteLine($"Cached large image icon: {iconWatch.ElapsedMilliseconds} ms");
    Check(ReferenceEquals(iconPng, warmIconPng), "repeated icon request uses cached bytes");
    using (var replacement = new System.Drawing.Bitmap(16, 16))
    using (var graphics = System.Drawing.Graphics.FromImage(replacement))
    { graphics.Clear(System.Drawing.Color.LimeGreen); replacement.Save(largeIcon, System.Drawing.Imaging.ImageFormat.Jpeg); }
    File.SetLastWriteTimeUtc(largeIcon, DateTime.UtcNow.AddSeconds(2));
    var replacedPng = ItemIcon.PngAsync(largeNode).GetAwaiter().GetResult()!;
    using (var updatedStream = new MemoryStream(replacedPng))
    using (var updated = System.Drawing.Image.FromStream(updatedStream))
        Check(updated.Width == 16 && updated.Height == 16, "modified image invalidates icon cache");
    Check(HotkeyParser.TryParse("Ctrl+Alt+Space", out var modifiers, out var key) && modifiers == 3 && key == 32, "global hotkey parse");
    Check(HotkeyParser.TryParse("Win+D1", out modifiers, out key) && modifiers == 8 && key == 49, "legacy digit key alias");
    Check(HotkeyParser.TryParse("Ctrl+OemPlus", out modifiers, out key) && key == 0xbb, "legacy OEM key alias");
    Check(!HotkeyParser.TryParse("Ctrl+Ctrl+A", out _, out _), "duplicate modifiers rejected");
    Check(!HotkeyParser.TryParse("Ctrl+", out _, out _), "missing key rejected");
    Check(!HotkeyParser.TryParse("Ctrl+LeftWindows", out _, out _), "modifier-only shortcut rejected");
    foreach (var code in new uint[] { 0x41, 0x35, 0x65, 0x70, 0xbb, 0x20, 0x0d })
        Check(HotkeyParser.TryParse(HotkeyParser.Format(15, code), out modifiers, out key) && modifiers == 15 && key == code, "recorded shortcut round trip " + code);
    var badShortcut = store.CreateDraft();
    badShortcut.Config.Items.Add(new MenuNode { Name = "Invalid send", Kind = MenuNodeKind.Action,
        Action = new MenuAction { Kind = ActionKind.SendShortcut, Shortcut = "Ctrl+" } });
    Check(!store.Validate(badShortcut.Config).IsValid, "invalid sent shortcut blocked before execution");
    Console.WriteLine("All configuration and icon checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
