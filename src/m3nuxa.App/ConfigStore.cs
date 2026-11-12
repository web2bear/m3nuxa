using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keys = Windows.System.VirtualKey;

namespace m3nuxa.App;

public sealed class LegacySettingsException : IOException
{
    public string SettingsPath { get; } = "";

    public LegacySettingsException() { }
    public LegacySettingsException(string message) : base(message) { }
    public LegacySettingsException(string message, Exception inner) : base(message, inner) { }
    private LegacySettingsException(string path, string message, Exception inner) : base(message, inner)
    {
        SettingsPath = Path.GetFullPath(path);
    }
    public static LegacySettingsException ForFile(string path, Exception inner) =>
        new(path, L.Format("LegacySettingsReadFailed", Path.GetFullPath(path), inner.Message), inner);
}

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
    private readonly string? configDirectory;
    private string? diskFingerprint;
    private AppConfig current = new();
    private long version = 1;

    public ConfigStore(string? path = null, string? selectionFile = null, string? configDirectory = null)
    {
        this.selectionFile = selectionFile;
        this.configDirectory = configDirectory is null ? null : Path.GetFullPath(configDirectory);
        if (this.configDirectory is not null) Directory.CreateDirectory(this.configDirectory);
        this.path = path ?? StartupPath(selectionFile, this.configDirectory is not null);
        if (this.configDirectory is not null) this.path = RequireCatalogPath(this.path);
        if (path is null && selectionFile is not null && File.Exists(selectionFile) && !File.Exists(this.path))
            throw new InvalidDataException($"Configuration file not found: {this.path}");
        if (File.Exists(this.path))
        {
            current = ReadConfig(this.path, Path.Combine(SettingsDirectory, "settings.json"));
            var validation = Validate(current);
            if (!validation.IsValid)
                throw new InvalidDataException($"{this.path}: {string.Join("; ", validation.Errors)}");
            diskFingerprint = Fingerprint(this.path);
        }
        else
        {
            var legacySettings = Path.Combine(SettingsDirectory, "settings.json");
            if (File.Exists(legacySettings)) current.Settings = ReadLegacySettings(legacySettings);
        }
    }

    public Func<string>? GlobalHotkey { get; set; }
    public Func<string>? PanelHotkey { get; set; }
    public string FilePath { get { lock (gate) return path; } }
    public string? ConfigDirectory => configDirectory;
    public string SettingsDirectory => selectionFile is null ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetDirectoryName(Path.GetFullPath(selectionFile))!;

    public static string StartupPath(string? selectionFile, bool selectFromCatalog = false)
    {
        if (selectionFile is null) return Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(selectionFile))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(selectionFile))!;
            var defaultPath = Path.Combine(directory, "config.json");
            if (!selectFromCatalog || File.Exists(defaultPath) || !Directory.Exists(directory)) return defaultPath;
            return Directory.EnumerateFiles(directory, "*.json")
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault() ?? defaultPath;
        }
        var selected = File.ReadAllText(selectionFile).Trim();
        if (string.IsNullOrWhiteSpace(selected)) throw new InvalidDataException($"Empty configuration selection: {selectionFile}");
        return Path.GetFullPath(selected, Path.GetDirectoryName(Path.GetFullPath(selectionFile))!);
    }
    public static void OfferLegacyMigration(string directory, string legacySelection, Func<string, string, bool> confirm)
    {
        var catalog = Path.GetFullPath(directory);
        var selection = Path.Combine(catalog, "active-config.txt");
        var marker = Path.Combine(catalog, ".legacy-migration-complete");
        if (File.Exists(selection) || File.Exists(marker)) return;
        var legacyPath = StartupPath(legacySelection);
        if (!File.Exists(legacyPath) || SamePath(Path.GetDirectoryName(legacyPath)!, catalog)) return;
        if (!confirm(legacyPath, catalog)) { File.WriteAllText(marker, "declined"); return; }

        var legacy = new ConfigStore(legacyPath, legacySelection);
        var config = legacy.Current;
        var files = Directory.EnumerateFiles(catalog, "*.json").ToArray();
        var previousSelection = files.Length == 0 ? null : StartupPath(selection, selectFromCatalog: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var known = ReadKnownName(file);
            if (!string.IsNullOrWhiteSpace(known)) names.Add(known.Trim());
        }
        var baseName = config.Name.Trim();
        var suffix = 2;
        while (names.Contains(config.Name.Trim())) config.Name = $"{baseName} ({suffix++})";
        var target = files.Length == 0 ? Path.Combine(catalog, "config.json") : Path.Combine(catalog, $"{Guid.NewGuid():N}.json");
        var temporary = Path.Combine(catalog, $".{Guid.NewGuid():N}.tmp");
        var created = false;
        var selectionCreated = false;
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temporary, target);
            created = true;
            if (previousSelection is not null)
            {
                using var stream = new FileStream(selection, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                selectionCreated = true;
                using var selected = new StreamWriter(stream);
                selected.Write(Path.GetFileName(previousSelection));
            }
            File.WriteAllText(marker, "copied");
        }
        catch
        {
            if (selectionCreated) try { File.Delete(selection); } catch { }
            if (created) try { File.Delete(target); } catch { }
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public AppConfig Current { get { lock (gate) return Clone(current); } }
    public long Version { get { lock (gate) return version; } }

    public ConfigDraft CreateDraft()
    {
        lock (gate) return new ConfigDraft { Config = Clone(current), BaseVersion = version, SourcePath = path, SourceFingerprint = diskFingerprint };
    }

    public ConfigDraft LoadDraft(string sourcePath)
    {
        lock (gate)
        {
            var source = configDirectory is null ? Path.GetFullPath(sourcePath) : RequireCatalogPath(sourcePath);
            var config = ReadConfig(source);
            var validation = Validate(config);
            if (!validation.IsValid) throw new InvalidDataException($"{source}: {string.Join("; ", validation.Errors)}");
            return new ConfigDraft { Config = config, BaseVersion = version, SourcePath = source, SourceFingerprint = Fingerprint(source) };
        }
    }

    public IReadOnlyList<ConfigEntry> ListConfigurations()
    {
        lock (gate)
        {
            if (configDirectory is null) throw new InvalidOperationException("A configuration directory is required.");
            return Directory.EnumerateFiles(configDirectory, "*.json").Select(file =>
            {
                var full = Path.GetFullPath(file);
                try
                {
                    var config = ReadConfig(full);
                    var check = Validate(config);
                    return new ConfigEntry(full, config.Name, SamePath(full, path), check.IsValid ? null : string.Join("; ", check.Errors));
                }
                catch (Exception ex) { return new ConfigEntry(full, Path.GetFileNameWithoutExtension(full), SamePath(full, path), ex.Message); }
            }).OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public ConfigDraft CreateConfigurationDraft(string name)
    {
        lock (gate)
        {
            if (configDirectory is null) throw new InvalidOperationException("A configuration directory is required.");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Configuration name is required.");
            EnsureUniqueName(name, null);
            return new ConfigDraft { Config = new AppConfig { Name = name }, BaseVersion = version,
                SourcePath = Path.Combine(configDirectory, $"{Guid.NewGuid():N}.json"), SourceFingerprint = null };
        }
    }

    public void DeleteActiveConfiguration(long expectedVersion, Func<AppConfig, string?>? preflight = null)
    {
        lock (gate)
        {
            if (expectedVersion != version) throw new InvalidOperationException(L.Get("ConfigDraftStale"));
            if (configDirectory is null || selectionFile is null)
                throw new InvalidOperationException("A configuration directory and selection file are required.");
            var oldPath = RequireCatalogPath(path);
            if (!File.Exists(oldPath)) throw new FileNotFoundException("Configuration not found.", oldPath);
            if (Fingerprint(oldPath) != diskFingerprint) throw new InvalidOperationException(L.Get("ConfigChangedOutside"));

            var next = ListConfigurations().Where(entry => !SamePath(entry.Path, oldPath))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            var newPath = next?.Path ?? Path.Combine(configDirectory, $"{Guid.NewGuid():N}.json");
            if (next?.Error is not null) throw new InvalidDataException($"{next.Path}: {next.Error}");
            var replacement = next is null ? new AppConfig() : ReadConfig(newPath);
            var validation = Validate(replacement);
            if (!validation.IsValid) throw new InvalidDataException(string.Join("; ", validation.Errors));
            if (preflight?.Invoke(replacement) is { } error) throw new InvalidOperationException(error);
            var nextFingerprint = next is null ? null : Fingerprint(newPath);
            var previousSelection = File.Exists(selectionFile) ? File.ReadAllBytes(selectionFile) : null;
            var created = false;
            var selectionChanged = false;
            try
            {
                if (next is null)
                {
                    using var output = new FileStream(newPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    created = true;
                    JsonSerializer.Serialize(output, replacement, JsonOptions);
                }
                var newFingerprint = Fingerprint(newPath);
                if (nextFingerprint is not null && nextFingerprint != newFingerprint)
                    throw new InvalidOperationException(L.Get("ConfigChangedOutside"));
                if (Fingerprint(oldPath) != diskFingerprint) throw new InvalidOperationException(L.Get("ConfigChangedOutside"));
                WriteSelection(Path.GetFileName(newPath));
                selectionChanged = true;
                File.Delete(oldPath);
                path = newPath;
                current = Clone(replacement);
                diskFingerprint = newFingerprint;
                version++;
            }
            catch (Exception ex)
            {
                try
                {
                    if (selectionChanged)
                    {
                        if (previousSelection is null) File.Delete(selectionFile);
                        else WriteSelectionBytes(previousSelection);
                    }
                    if (created && File.Exists(newPath)) File.Delete(newPath);
                }
                catch (Exception rollback) { throw new IOException($"{ex.Message} (rollback: {rollback.Message})", ex); }
                throw;
            }
        }
    }

    private void WriteSelection(string selected) => WriteSelectionBytes(System.Text.Encoding.UTF8.GetBytes(selected));

    private void WriteSelectionBytes(byte[] contents)
    {
        var temporary = selectionFile! + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, contents); File.Move(temporary, selectionFile!, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string RequireCatalogPath(string candidate)
    {
        if (configDirectory is null) throw new InvalidOperationException("A configuration directory is required.");
        var full = Path.GetFullPath(candidate);
        if (!SamePath(Path.GetDirectoryName(full)!, configDirectory) || !string.Equals(Path.GetExtension(full), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Configuration must be a JSON file in the configuration directory.");
        return full;
    }

    private static bool SamePath(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar));

    private void EnsureUniqueName(string name, string? exceptPath)
    {
        if (configDirectory is null) return;
        foreach (var file in Directory.EnumerateFiles(configDirectory, "*.json"))
        {
            if (exceptPath is not null && SamePath(file, exceptPath)) continue;
            var existingName = ReadKnownName(file);
            if (!string.IsNullOrWhiteSpace(existingName) && string.Equals(existingName.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A configuration with this name already exists.");
        }
    }

    private static string? ReadKnownName(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var property = document.RootElement.EnumerateObject()
                .FirstOrDefault(item => item.Name.Equals("Name", StringComparison.OrdinalIgnoreCase));
            return property.Name is null ? Path.GetFileNameWithoutExtension(file)
                : property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void SaveDraft(ConfigDraft draft, string destinationPath)
    {
        var full = configDirectory is null ? Path.GetFullPath(destinationPath) : RequireCatalogPath(destinationPath);
        if (configDirectory is not null) EnsureUniqueName(draft.Config.Name, full);
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
                if (!File.Exists(path) && diskFingerprint is not null)
                    return new(false, $"Configuration file not found: {path}");
                var candidate = File.Exists(path) ? ReadConfig(path, Path.Combine(SettingsDirectory, "settings.json")) : new AppConfig();
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

                if (draft.OriginPath is not null)
                {
                    var origin = configDirectory is null ? Path.GetFullPath(draft.OriginPath) : RequireCatalogPath(draft.OriginPath);
                    if ((File.Exists(origin) ? Fingerprint(origin) : null) != draft.OriginFingerprint)
                        return ApplyResult.Failed(L.Get("ConfigChangedOutside"), check);
                }

                var target = destinationPath is null ? Path.GetFullPath(draft.SourcePath ?? path) : Path.GetFullPath(destinationPath);
                if (configDirectory is not null)
                {
                    target = RequireCatalogPath(target);
                    EnsureUniqueName(draft.Config.Name, target);
                    if (destinationPath is null && draft.SourcePath is not null &&
                        (File.Exists(target) ? Fingerprint(target) : null) != draft.SourceFingerprint)
                        return ApplyResult.Failed(L.Get("ConfigChangedOutside"), check);
                }
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

                if (!SamePath(target, path) && selectionFile is not null)
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
                if (!switched && savedTarget is not null && !SamePath(savedTarget, path))
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

    public static ConfigStore CreateDefaultAfterInvalid(string? configPath = null, string? selectionFile = null, string? configDirectory = null)
    {
        var target = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        // Do not replace even an invalid config if its separate legacy settings
        // cannot be migrated; report that file first while preserving both inputs.
        var settingsDirectory = selectionFile is null
            ? Path.GetDirectoryName(Path.GetFullPath(target))!
            : Path.GetDirectoryName(Path.GetFullPath(selectionFile))!;
        var legacySettings = Path.Combine(settingsDirectory, "settings.json");
        if (File.Exists(legacySettings)) _ = ReadLegacySettings(legacySettings);
        if (File.Exists(target))
        {
            var copy = $"{target}.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
            File.Copy(target, copy);
            File.Delete(target);
        }
        return new ConfigStore(target, selectionFile, configDirectory);
    }

    private static AppConfig ReadConfig(string file, string? legacySettingsPath = null)
    {
        var json = File.ReadAllText(file);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException(L.Get("ConfigJsonEmpty"));
        bool hasSettings = document.RootElement.EnumerateObject().Any(property => property.Name.Equals("Settings", StringComparison.OrdinalIgnoreCase));
        bool hasSchema = document.RootElement.EnumerateObject().Any(property => property.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase));
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
            ?? throw new InvalidDataException(L.Get("ConfigJsonEmpty"));
        if (!document.RootElement.EnumerateObject().Any(property => property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)))
            config.Name = Path.GetFileNameWithoutExtension(file);
        // Legacy settings belong to the active installation only, never an imported file.
        // Migration remains in memory until Apply creates a backup and saves the complete file.
        if (!hasSettings && (!hasSchema || config.SchemaVersion is 1 or 2))
        {
            config.Settings = legacySettingsPath is not null && File.Exists(legacySettingsPath)
                ? ReadLegacySettings(legacySettingsPath)
                : new AppSettings { MainHotkey = config.MainHotkey };
        }
        else if (!hasSettings || config.Settings is null)
            throw new InvalidDataException("Configuration settings are missing.");
        if (config.SchemaVersion is 1 or 2) config.SchemaVersion = 3;
        return config;
    }

    private static AppSettings ReadLegacySettings(string file)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException(L.Get("Settings/EmptyFile"));
            SettingsStore.Validate(settings, L.Get);
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw LegacySettingsException.ForFile(file, ex);
        }
    }

    private static AppConfig Clone(AppConfig value) =>
        new() { SchemaVersion = value.SchemaVersion, Name = value.Name, MainHotkey = value.MainHotkey, Settings = value.Settings with { }, Items = JsonSerializer.Deserialize<List<MenuNode>>(JsonSerializer.Serialize(value.Items, JsonOptions), JsonOptions)! };

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
        if (string.IsNullOrWhiteSpace(config.Name)) errors.Add("Configuration name is required.");
        if (config.SchemaVersion != 3) errors.Add(L.Get("ConfigSchemaUnsupported"));
        if (config.Items is null) errors.Add(L.Get("ConfigTreeMissing"));
        var hotkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.Settings is null) errors.Add("Configuration settings are missing.");
        else try { SettingsStore.Validate(config.Settings, L.Get); } catch (InvalidDataException ex) { errors.Add(ex.Message); }
        var mainHotkey = config.Settings?.MainHotkey ?? "";
        if (!string.IsNullOrWhiteSpace(mainHotkey))
        {
            if (TryNormalizeHotkey(mainHotkey, out var normalized)) hotkeys.Add(normalized);
            else errors.Add(L.Get("ConfigInvalidMainShortcut"));
        }
        var panelHotkey = config.Settings?.PanelHotkey ?? "";
        if (!string.IsNullOrWhiteSpace(panelHotkey) && TryNormalizeHotkey(panelHotkey, out var normalizedPanel) && !hotkeys.Add(normalizedPanel))
            errors.Add(L.Format("ConfigDuplicateHotkey", L.Get("ConfigMenu")));
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
                    if (node.Steps is null) errors.Add(L.Format("ConfigActionMissing", label));
                    else foreach (var step in node.Steps)
                    {
                        if (step is null) errors.Add(L.Format("ConfigActionMissing", label));
                        else if (!Enum.IsDefined(step.Kind)) errors.Add(L.Format("ConfigUnknownAction", label));
                    }
                }
                else if (node.Steps is null || node.Steps.Count > 0) errors.Add(L.Format("ConfigActionOnNonAction", label));
                if (node.Kind != MenuNodeKind.Group && node.Children?.Count > 0) errors.Add(L.Format("ConfigChildrenOnNonGroup", label));
                if (node.Children is null) errors.Add(L.Format("ConfigChildrenMissing", label));
                else if (node.Kind == MenuNodeKind.Group) Visit(node.Children, label);
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
