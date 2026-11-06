using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keys = Windows.System.VirtualKey;

namespace m3nuxa.App;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object gate = new();
    private string path;
    private readonly string? selectionFile;
    private string? diskFingerprint;
    private AppConfig current = new();
    private long version = 1;

    public ConfigStore(string? path = null, string? selectionFile = null)
    {
        this.selectionFile = selectionFile;
        this.path = path ?? StartupPath(selectionFile);
        if (path is null && selectionFile is not null && File.Exists(selectionFile) && !File.Exists(this.path))
            throw new InvalidDataException($"Configuration file not found: {this.path}");
        if (File.Exists(this.path))
        {
            current = ReadConfig(this.path);
            var validation = Validate(current);
            if (!validation.IsValid)
                throw new InvalidDataException($"{this.path}: {string.Join("; ", validation.Errors)}");
            diskFingerprint = Fingerprint(this.path);
        }
    }

    public Func<string>? GlobalHotkey { get; set; }
    public string FilePath { get { lock (gate) return path; } }
    public string SettingsDirectory => selectionFile is null ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetDirectoryName(Path.GetFullPath(selectionFile))!;

    public static string StartupPath(string? selectionFile)
    {
        if (selectionFile is null || !File.Exists(selectionFile)) return Path.Combine(AppContext.BaseDirectory, "config.json");
        var selected = File.ReadAllText(selectionFile).Trim();
        if (string.IsNullOrWhiteSpace(selected)) throw new InvalidDataException($"Empty configuration selection: {selectionFile}");
        return Path.GetFullPath(selected, Path.GetDirectoryName(Path.GetFullPath(selectionFile))!);
    }
    public AppConfig Current { get { lock (gate) return Clone(current); } }
    public long Version { get { lock (gate) return version; } }

    public ConfigDraft CreateDraft()
    {
        lock (gate) return new ConfigDraft { Config = Clone(current), BaseVersion = version };
    }

    public ConfigDraft LoadDraft(string sourcePath)
    {
        var config = ReadConfig(sourcePath);
        lock (gate) return new ConfigDraft { Config = config, BaseVersion = version };
    }

    public void SaveDraft(ConfigDraft draft, string destinationPath)
    {
        var full = Path.GetFullPath(destinationPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(full, Path.GetFullPath(path)))
            throw new InvalidOperationException(L.Get("ConfigDraftOverwrite"));
        File.WriteAllText(full, JsonSerializer.Serialize(draft.Config, JsonOptions));
    }

    public ReloadResult Reload()
    {
        lock (gate)
        {
            try
            {
                var candidate = File.Exists(path) ? ReadConfig(path) : new AppConfig();
                var check = Validate(candidate);
                if (!check.IsValid) return new(false, $"{path}: {string.Join("; ", check.Errors)}");
                current = candidate;
                diskFingerprint = File.Exists(path) ? Fingerprint(path) : null;
                version++;
                return new(true, null);
            }
            catch (Exception ex) { return new(false, $"{path}: {ex.Message}"); }
        }
    }

    public ApplyResult Apply(ConfigDraft draft) => ApplyTo(draft, null);
    public ApplyResult SaveAs(ConfigDraft draft, string destinationPath) => ApplyTo(draft, destinationPath);

    private ApplyResult ApplyTo(ConfigDraft draft, string? destinationPath)
    {
        lock (gate)
        {
            var check = Validate(draft.Config);
            if (!check.IsValid) return ApplyResult.Failed(string.Join("; ", check.Errors), check);
            if (draft.BaseVersion != version)
                return ApplyResult.Failed(L.Get("ConfigDraftStale"), check);

            string? temporary = null;
            string? savedTarget = null;
            string? targetBackup = null;
            bool targetWasCreated = false;
            bool switched = false;
            try
            {
                var actualFingerprint = File.Exists(path) ? Fingerprint(path) : null;
                if (actualFingerprint != diskFingerprint)
                    return ApplyResult.Failed(L.Get("ConfigChangedOutside"), check);

                var target = destinationPath is null ? Path.GetFullPath(path) : Path.GetFullPath(destinationPath);
                savedTarget = target;
                var directory = Path.GetDirectoryName(target)!;
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temporary, JsonSerializer.Serialize(draft.Config, JsonOptions));
                var replacementFingerprint = Fingerprint(temporary);
                var replacementConfig = Clone(draft.Config);

                if (File.Exists(target))
                {
                    var backupDirectory = Path.Combine(directory, "backups");
                    Directory.CreateDirectory(backupDirectory);
                    var backup = Path.Combine(backupDirectory,
                        $"config-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.json");
                    File.Copy(target, backup);
                    targetBackup = backup;
                    File.Replace(temporary, target, null);
                    temporary = null;
                    try { PruneBackups(backupDirectory); } catch { /* Retention cleanup cannot undo a successful apply. */ }
                }
                else
                {
                    File.Move(temporary, target);
                    targetWasCreated = true;
                    temporary = null;
                }

                if (destinationPath is not null && selectionFile is not null)
                {
                    var selectionDirectory = Path.GetDirectoryName(Path.GetFullPath(selectionFile))!;
                    var relative = Path.GetRelativePath(selectionDirectory, target);
                    var selected = relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative) ? target : relative;
                    var selectionTemp = selectionFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllText(selectionTemp, selected); File.Move(selectionTemp, selectionFile, true); }
                    finally { if (File.Exists(selectionTemp)) File.Delete(selectionTemp); }
                }
                path = target;
                switched = true;
                current = replacementConfig;
                diskFingerprint = replacementFingerprint;
                version++;
                return new(true, null, check);
            }
            catch (Exception ex)
            {
                if (destinationPath is not null && !switched && savedTarget is not null)
                {
                    try
                    {
                        if (targetBackup is not null) File.Copy(targetBackup, savedTarget, true);
                        else if (targetWasCreated) File.Delete(savedTarget);
                    }
                    catch (Exception rollback) { return ApplyResult.Failed($"{ex.Message} (rollback: {rollback.Message})", check); }
                }
                return ApplyResult.Failed(ex.Message, check);
            }
            finally { if (temporary is not null) try { File.Delete(temporary); } catch { } }
        }
    }

    public string? PreserveInvalidFile()
    {
        lock (gate)
        {
            if (!File.Exists(path)) return null;
            var copy = $"{path}.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
            File.Copy(path, copy);
            return copy;
        }
    }

    public static ConfigStore CreateDefaultAfterInvalid(string? configPath = null, string? selectionFile = null)
    {
        var target = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(target))
        {
            var copy = $"{target}.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
            File.Copy(target, copy);
            File.Delete(target);
        }
        return new ConfigStore(target, selectionFile);
    }

    private static AppConfig ReadConfig(string file)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), JsonOptions)
            ?? throw new InvalidDataException(L.Get("ConfigJsonEmpty"));
        // Migration is in memory; Apply creates a backup before replacing the source file.
        if (config.SchemaVersion == 1) config.SchemaVersion = 2;
        return config;
    }

    private static AppConfig Clone(AppConfig value) =>
        new() { SchemaVersion = value.SchemaVersion, MainHotkey = value.MainHotkey, Items = JsonSerializer.Deserialize<List<MenuNode>>(JsonSerializer.Serialize(value.Items, JsonOptions), JsonOptions)! };

    private static string Fingerprint(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void PruneBackups(string directory)
    {
        foreach (var old in Directory.EnumerateFiles(directory, "config-*.json")
                     .OrderByDescending(File.GetCreationTimeUtc).Skip(10))
            try { File.Delete(old); } catch { }
    }

    public ValidationResult Validate(AppConfig config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        if (config.SchemaVersion != 2) errors.Add(L.Get("ConfigSchemaUnsupported"));
        if (config.Items is null) errors.Add(L.Get("ConfigTreeMissing"));
        var hotkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mainHotkey = GlobalHotkey?.Invoke() ?? "";
        if (!string.IsNullOrWhiteSpace(mainHotkey))
        {
            if (TryNormalizeHotkey(mainHotkey, out var normalized)) hotkeys.Add(normalized);
            else errors.Add(L.Get("ConfigInvalidMainShortcut"));
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.Items is not null) Visit(config.Items, L.Get("ConfigMenu"));
        return new(errors, warnings);

        void Visit(List<MenuNode> nodes, string parent)
        {
            foreach (var node in nodes)
            {
                if (node is null) { errors.Add(L.Format("ConfigEmptyItem", parent)); continue; }
                var label = string.IsNullOrWhiteSpace(node.Name) ? parent : $"{parent}/{node.Name}";
                if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id)) errors.Add(L.Format("ConfigDuplicateId", label));
                if (!Enum.IsDefined(node.Kind)) errors.Add(L.Format("ConfigUnknownKind", label));
                if (node.Kind != MenuNodeKind.Separator && string.IsNullOrWhiteSpace(node.Name)) errors.Add(L.Format("ConfigNameRequired", label));
                if (node.Kind == MenuNodeKind.Separator && !string.IsNullOrWhiteSpace(node.Hotkey)) errors.Add(L.Format("ConfigSeparatorHotkey", label));
                if (!string.IsNullOrWhiteSpace(node.Hotkey))
                {
                    if (!TryNormalizeHotkey(node.Hotkey, out var normalized)) errors.Add(L.Format("ConfigInvalidHotkey", label));
                    else if (!hotkeys.Add(normalized)) errors.Add(L.Format("ConfigDuplicateHotkey", label));
                }
                if (node.Kind == MenuNodeKind.Action)
                {
                    if (node.Steps is null || node.Steps.Count == 0) errors.Add(L.Format("ConfigActionMissing", label));
                    else for (var i = 0; i < node.Steps.Count; i++)
                    {
                        var step = node.Steps[i];
                        var stepLabel = $"{label} / {L.Get("ConfigStep")} {i + 1}";
                        if (step is null) errors.Add(L.Format("ConfigActionMissing", stepLabel));
                        else ValidateAction(step, stepLabel);
                    }
                }
                else if (node.Steps is null || node.Steps.Count > 0) errors.Add(L.Format("ConfigActionOnNonAction", label));
                if (node.Kind != MenuNodeKind.Group && node.Children?.Count > 0) errors.Add(L.Format("ConfigChildrenOnNonGroup", label));
                if (node.Children is null) errors.Add(L.Format("ConfigChildrenMissing", label));
                else if (node.Kind == MenuNodeKind.Group) Visit(node.Children, label);
            }
        }

        void ValidateAction(MenuAction action, string label)
        {
            if (!Enum.IsDefined(action.Kind)) { errors.Add(L.Format("ConfigUnknownAction", label)); return; }
            if (action.Kind == ActionKind.Delay)
            {
                if (action.DelayMilliseconds < 0) errors.Add(L.Format("ConfigDelayInvalid", label));
                return;
            }
            if (action.Kind == ActionKind.SetClipboardText) return;
            if (action.Kind is ActionKind.RunProgram or ActionKind.RunScript && action.TimeoutMilliseconds is <= 0)
                errors.Add(L.Format("ConfigTimeoutInvalid", label));
            if (action.Kind == ActionKind.SendShortcut)
            {
                if (string.IsNullOrWhiteSpace(action.Shortcut)) errors.Add(L.Format("ConfigShortcutRequired", label));
                else if (!HotkeyParser.TryParse(action.Shortcut, out _, out _)) errors.Add(L.Format("ConfigShortcutInvalid", label));
                return;
            }
            if (action.Kind is ActionKind.InsertText or ActionKind.PasteText)
            {
                if (string.IsNullOrEmpty(action.Text)) errors.Add(L.Format("ConfigSnippetRequired", label));
                return;
            }
            if (string.IsNullOrWhiteSpace(action.Path)) { errors.Add(L.Format("ConfigPathRequired", label)); return; }
            if (action.Kind == ActionKind.OpenItem && Uri.TryCreate(action.Path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return;
            if (action.Kind is ActionKind.OpenItem or ActionKind.OpenFolder or ActionKind.RunProgram or ActionKind.RunScript or ActionKind.CopySelection or ActionKind.MoveSelection)
            {
                if (!action.Path.Contains('{') && !File.Exists(action.Path) && !Directory.Exists(action.Path))
                    warnings.Add(L.Format("ConfigPathUnavailable", label, action.Path));
            }
        }
    }

    private static bool TryNormalizeHotkey(string value, out string normalized)
    {
        normalized = "";
        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        uint modifiers = 0;
        Keys key = Keys.None;
        foreach (var part in parts)
        {
            uint modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => 2,
                "alt" => 1,
                "shift" => 4,
                "win" => 8,
                _ => 0
            };
            if (modifier != 0)
            {
                if (key != Keys.None || (modifiers & modifier) != 0) return false;
                modifiers |= modifier;
                continue;
            }
            if (key != Keys.None || !HotkeyParser.TryKey(part, out var parsed)
                || HotkeyParser.IsModifier((uint)parsed))
                return false;
            key = parsed;
        }
        if (key == Keys.None) return false;
        normalized = $"{modifiers}:{(int)key & 0xffff}";
        return true;
    }
}
