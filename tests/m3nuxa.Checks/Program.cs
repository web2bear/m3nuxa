using m3nuxa.App;
using System.Reflection;

if (args.Length == 2 && args[0] == "--open-folder")
{
    var result = await ActionExecutor.ExecuteAsync(
        new MenuAction { Kind = ActionKind.OpenFolder, Path = args[1] },
        new InvocationContext(IntPtr.Zero, [], null));
    if (!result.Success) throw new Exception($"OpenFolder failed: {result.Error}");
    return;
}

var root = Path.Combine(Path.GetTempPath(), "m3nuxa-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    WorkflowModelChecks.Run(root);
    UnifiedConfigurationChecks.Run(root);
    ConfigCatalogChecks.Run(root);
    WorkflowExecutionChecks.Run(root);
    AgentPipeWorkflowChecks.Run(root);
    CopyConflictChecks.Run(root);
    LocalizationChecks.Run();
    DropGeometryChecks.Run();
    EditorDraftChecks.Run();
    var path = Path.Combine(root, "config.json");
    var store = new ConfigStore(path);
    var legacyPath = Path.Combine(root, "legacy.json");
    File.WriteAllText(legacyPath, "{\"MainHotkey\":\"Ctrl+Shift+F8\",\"Items\":[]}");
    Check(new ConfigStore(legacyPath).Current.MainHotkey == "Ctrl+Shift+F8", "legacy shortcut read from existing JSON");
    var settingsPath = Path.Combine(root, "settings.json");
    var settings = new SettingsStore(settingsPath, "Ctrl+Shift+F8");
    Check(settings.Current.MainHotkey == "Ctrl+Shift+F8", "legacy global shortcut migrated");
    settings.Save(settings.Current with { MainHotkey = "Ctrl+Alt+F9", MenuFontSize = 16 });
    Check(new SettingsStore(settingsPath, "Ctrl+Shift+F8").Current == settings.Current, "settings survive restart and migration does not overwrite");
    var settingsDraft = store.CreateDraft();
    settingsDraft.Config.Settings = settings.Current;
    Check(store.Apply(settingsDraft).Success && store.Current.Settings.MainHotkey == "Ctrl+Alt+F9", "settings apply with menu draft");
    Check(new ConfigStore(path).Current.Settings == settings.Current, "unified settings survive restart");
    Check(File.ReadAllText(path).Contains("\"Settings\""), "new menu file includes settings");
    var conflictDraft = store.CreateDraft();
    conflictDraft.Config.Items.Add(new MenuNode { Name = "Conflict", Kind = MenuNodeKind.Group, Hotkey = "Control+Alt+F9" });
    Check(!store.Validate(conflictDraft.Config).IsValid, "item shortcut validated against draft settings");
    var conflictingGlobals = store.CreateDraft();
    conflictingGlobals.Config.Settings = conflictingGlobals.Config.Settings with { PanelHotkey = "Control+Alt+F9" };
    Check(!store.Validate(conflictingGlobals.Config).IsValid, "global shortcuts cannot conflict");
    try { settings.Save(settings.Current with { MenuFontSize = double.NaN }); throw new Exception("invalid font accepted"); }
    catch (InvalidDataException) { Check(settings.Current.MenuFontSize == 16, "invalid settings preserve current state"); }
    File.Delete(path);
    store = new ConfigStore(path);
    var first = store.CreateDraft();
    first.Config.Items.Add(new MenuNode { Name = "Work", Kind = MenuNodeKind.Group });
    Check(store.Apply(first).Success, "first apply");
    var preservedVersion = store.Version;
    File.Move(path, path + ".moved");
    Check(!store.Reload().Success && store.Version == preservedVersion && store.Current.Items.Count == 1, "missing reload preserves active configuration");
    File.Move(path + ".moved", path);
    var stale = store.CreateDraft();
    var next = store.CreateDraft();
    next.Config.Items.Add(new MenuNode { Name = "Other", Kind = MenuNodeKind.Group });
    Check(store.Apply(next).Success, "second apply");
    Check(!store.Apply(stale).Success, "stale draft blocked");
    var saveAsRoot = Path.Combine(root, "save-as");
    Directory.CreateDirectory(saveAsRoot);
    var selectionFile = Path.Combine(saveAsRoot, "active-config.txt");
    var originalFile = Path.Combine(saveAsRoot, "original.json");
    var alternateFile = Path.Combine(saveAsRoot, "other.json");
    var saveAsStore = new ConfigStore(originalFile, selectionFile);
    Check(saveAsStore.Apply(saveAsStore.CreateDraft()).Success, "save-as initial configuration");
    var staleSaveAs = saveAsStore.CreateDraft();
    var saveAsDraft = saveAsStore.CreateDraft();
    saveAsDraft.Config.Items.Add(new MenuNode { Name = "Saved as", Kind = MenuNodeKind.Group });
    Check(saveAsStore.SaveAs(saveAsDraft, alternateFile).Success, "save as succeeds");
    Check(saveAsStore.FilePath == alternateFile && ConfigStore.StartupPath(selectionFile) == alternateFile, "save as selects persistent active file");
    Check(new ConfigStore(selectionFile: selectionFile).Current.Items.Single().Name == "Saved as", "save as survives restart");
    Check(!saveAsStore.SaveAs(staleSaveAs, Path.Combine(saveAsRoot, "stale.json")).Success, "save as rejects stale draft");
    var followingDraft = saveAsStore.CreateDraft();
    followingDraft.Config.Items.Add(new MenuNode { Name = "Next save", Kind = MenuNodeKind.Group });
    Check(saveAsStore.Apply(followingDraft).Success && new ConfigStore(alternateFile).Current.Items.Count == 2, "subsequent save uses new file");
    Check(new ConfigStore(originalFile).Current.Items.Count == 0, "save as leaves original file unchanged");
    var existingTarget = Path.Combine(saveAsRoot, "existing.json");
    File.WriteAllText(existingTarget, "previous contents");
    var brokenSelection = new ConfigStore(originalFile, Path.Combine(saveAsRoot, "missing-directory", "active-config.txt"));
    Check(!brokenSelection.SaveAs(brokenSelection.CreateDraft(), existingTarget).Success, "selection persistence error blocks save as");
    Check(File.ReadAllText(existingTarget) == "previous contents" && brokenSelection.FilePath == originalFile, "failed save as restores existing target and active path");
    var newTarget = Path.Combine(saveAsRoot, "failed-new.json");
    Check(!brokenSelection.SaveAs(brokenSelection.CreateDraft(), newTarget).Success && !File.Exists(newTarget), "failed save as removes newly created target");
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
    Check(await ItemIcon.PngAsync(new MenuNode { Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.OpenFolder, Path = "{knownFolder:Downloads}" } }) is { Length: > 0 }, "known folder menu icon");
    Check(await ItemIcon.PngAsync(new MenuNode { Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.SendShortcut, Shortcut = "F16" } }) is { Length: > 0 }, "shortcut menu icon");
    var offlineIcon = ItemIcon.PngAsync(new MenuNode { Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.OpenFolder, Path = @"\\m3nuxa-offline-test.invalid\share" } });
    var followingIcon = ItemIcon.PngAsync(new MenuNode { Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.SendShortcut, Shortcut = "F16" } });
    await Task.WhenAny(Task.WhenAll(offlineIcon, followingIcon), Task.Delay(2000));
    Check(offlineIcon.IsCompletedSuccessfully && followingIcon.IsCompletedSuccessfully && offlineIcon.Result is { Length: > 0 } && followingIcon.Result is { Length: > 0 }, "offline network icon does not block following icons");
    _ = ItemIcon.PngAsync(new MenuNode { Kind = MenuNodeKind.Group, IconPath = @"\\m3nuxa-offline-test.invalid\share\icon.png" });
    var afterRemoteCustom = ItemIcon.PngAsync(new MenuNode { Kind = MenuNodeKind.Action, Action = new MenuAction { Kind = ActionKind.SendShortcut, Shortcut = "F16" } });
    await Task.WhenAny(afterRemoteCustom, Task.Delay(2000));
    Check(afterRemoteCustom.IsCompletedSuccessfully && afterRemoteCustom.Result is { Length: > 0 }, "network custom icon does not block local icons");
    using (var programIcon = ItemIcon.Load(new MenuNode { IconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe") }))
        Check(programIcon is { Width: > 0 }, "executable shell icon, not Image.FromFile");
    var customIcon = Path.Combine(root, "custom.png");
    using (var bitmap = new System.Drawing.Bitmap(16, 16))
    { bitmap.SetPixel(0, 0, System.Drawing.Color.Magenta); bitmap.Save(customIcon); }
    var iconNode = new MenuNode { IconPath = customIcon, Action = new MenuAction { Path = root } };
    using (var loaded = ItemIcon.Load(iconNode))
        Check(loaded?.GetPixel(0, 0).ToArgb() == System.Drawing.Color.Magenta.ToArgb(), "custom image overrides shell icon");
    iconNode.IconColor = "#00FF00";
    using (var loaded = ItemIcon.Load(iconNode))
        Check(loaded?.GetPixel(0, 0).ToArgb() == System.Drawing.Color.Magenta.ToArgb(), "external image keeps its original color");
    var colored = new MenuNode { IconPath = Path.Combine("icon-packs", "Boxicons", "bx-home.png"), IconColor = "#F04020" };
    using (var source = ItemIcon.Load(new MenuNode { IconPath = colored.IconPath }))
    using (var result = ItemIcon.Load(colored))
    {
        Check(source is not null && result is not null, "bundled colored icon loads");
        var points = from y in Enumerable.Range(0, result!.Height) from x in Enumerable.Range(0, result.Width) select (x, y);
        var opaque = points.First(p => source!.GetPixel(p.x, p.y).A == 255);
        var pixel = result!.GetPixel(opaque.x, opaque.y);
        Check(pixel.R == 240 && pixel.G == 64 && pixel.B == 32 && pixel.A == 255, "bundled icon uses selected color");
        Check(points.All(p => source!.GetPixel(p.x, p.y).A == result.GetPixel(p.x, p.y).A), "color preserves transparency");
    }
    var redBytes = ItemIcon.PngAsync(colored).GetAwaiter().GetResult();
    colored.IconColor = "#20C040";
    var greenBytes = ItemIcon.PngAsync(colored).GetAwaiter().GetResult();
    Check(redBytes is not null && greenBytes is not null && !redBytes.SequenceEqual(greenBytes), "icon cache distinguishes colors");
    var roundTrip = System.Text.Json.JsonSerializer.Deserialize<MenuNode>(System.Text.Json.JsonSerializer.Serialize(colored));
    Check(roundTrip?.IconColor == "#20C040", "icon color survives configuration serialization");
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
    Check(store.Validate(badShortcut.Config).IsValid, "invalid sent shortcut can be saved for later editing");
    Console.WriteLine("All configuration and icon checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
}
