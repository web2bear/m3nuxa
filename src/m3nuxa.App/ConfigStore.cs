using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

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
    private readonly string path;
    private string? diskFingerprint;
    private AppConfig current = new();
    private long version = 1;

    public ConfigStore(string? path = null)
    {
        this.path = path ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(this.path))
        {
            current = ReadConfig(this.path);
            var validation = Validate(current);
            if (!validation.IsValid)
                throw new InvalidDataException($"{this.path}: {string.Join("; ", validation.Errors)}");
            diskFingerprint = Fingerprint(this.path);
        }
    }

    public string FilePath => path;
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
            throw new InvalidOperationException("Черновик нельзя записать поверх действующей конфигурации.");
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

    public ApplyResult Apply(ConfigDraft draft)
    {
        lock (gate)
        {
            var check = Validate(draft.Config);
            if (!check.IsValid) return ApplyResult.Failed(string.Join("; ", check.Errors), check);
            if (draft.BaseVersion != version)
                return ApplyResult.Failed("Черновик устарел. Сохраните его отдельно или загрузите актуальную конфигурацию.", check);

            string? temporary = null;
            try
            {
                var actualFingerprint = File.Exists(path) ? Fingerprint(path) : null;
                if (actualFingerprint != diskFingerprint)
                    return ApplyResult.Failed("Файл конфигурации был изменён извне. Явно перезагрузите его перед применением.", check);

                var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
                Directory.CreateDirectory(directory);
                temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temporary, JsonSerializer.Serialize(draft.Config, JsonOptions));
                var replacementFingerprint = Fingerprint(temporary);
                var replacementConfig = Clone(draft.Config);

                if (File.Exists(path))
                {
                    var backupDirectory = Path.Combine(directory, "backups");
                    Directory.CreateDirectory(backupDirectory);
                    var backup = Path.Combine(backupDirectory,
                        $"config-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.json");
                    File.Copy(path, backup);
                    File.Replace(temporary, path, null);
                    temporary = null;
                    try { PruneBackups(backupDirectory); } catch { /* Retention cleanup cannot undo a successful apply. */ }
                }
                else
                {
                    File.Move(temporary, path);
                    temporary = null;
                }

                current = replacementConfig;
                diskFingerprint = replacementFingerprint;
                version++;
                return new(true, null, check);
            }
            catch (Exception ex) { return ApplyResult.Failed(ex.Message, check); }
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

    public static ConfigStore CreateDefaultAfterInvalid(string? configPath = null)
    {
        var target = configPath ?? Path.Combine(AppContext.BaseDirectory, "config.json");
        if (File.Exists(target))
        {
            var copy = $"{target}.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
            File.Copy(target, copy);
            File.Delete(target);
        }
        return new ConfigStore(target);
    }

    private static AppConfig ReadConfig(string file) =>
        JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), JsonOptions)
        ?? throw new InvalidDataException("JSON не содержит конфигурацию.");

    private static AppConfig Clone(AppConfig value) =>
        JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

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
        if (config.SchemaVersion != 1) errors.Add("Неподдерживаемая версия схемы.");
        if (config.Items is null) errors.Add("Отсутствует дерево меню.");
        var hotkeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(config.MainHotkey))
        {
            if (TryNormalizeHotkey(config.MainHotkey, out var normalized)) hotkeys.Add(normalized);
            else errors.Add("Основное меню: некорректное сочетание клавиш.");
        }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.Items is not null) Visit(config.Items, "Меню");
        return new(errors, warnings);

        void Visit(List<MenuNode> nodes, string parent)
        {
            foreach (var node in nodes)
            {
                if (node is null) { errors.Add($"{parent}: пустой пункт."); continue; }
                var label = string.IsNullOrWhiteSpace(node.Name) ? parent : $"{parent}/{node.Name}";
                if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id)) errors.Add($"{label}: пустой или повторяющийся ID.");
                if (!Enum.IsDefined(node.Kind)) errors.Add($"{label}: неизвестный тип пункта.");
                if (node.Kind != MenuNodeKind.Separator && string.IsNullOrWhiteSpace(node.Name)) errors.Add($"{label}: требуется имя.");
                if (node.Kind == MenuNodeKind.Separator && !string.IsNullOrWhiteSpace(node.Hotkey)) errors.Add($"{label}: разделителю нельзя назначить сочетание.");
                if (!string.IsNullOrWhiteSpace(node.Hotkey))
                {
                    if (!TryNormalizeHotkey(node.Hotkey, out var normalized)) errors.Add($"{label}: некорректное сочетание клавиш.");
                    else if (!hotkeys.Add(normalized)) errors.Add($"{label}: повторяющееся сочетание.");
                }
                if (node.Kind == MenuNodeKind.Action)
                {
                    if (node.Action is null) errors.Add($"{label}: отсутствует действие.");
                    else ValidateAction(node.Action, label);
                }
                else if (node.Action is not null) errors.Add($"{label}: действие допустимо только для пункта действия.");
                if (node.Kind != MenuNodeKind.Group && node.Children?.Count > 0) errors.Add($"{label}: дочерние пункты допустимы только для группы.");
                if (node.Children is null) errors.Add($"{label}: отсутствует список дочерних пунктов.");
                else if (node.Kind == MenuNodeKind.Group) Visit(node.Children, label);
            }
        }

        void ValidateAction(MenuAction action, string label)
        {
            if (!Enum.IsDefined(action.Kind)) { errors.Add($"{label}: неизвестное действие."); return; }
            if (action.Kind == ActionKind.SendShortcut)
            {
                if (string.IsNullOrWhiteSpace(action.Shortcut)) errors.Add($"{label}: требуется сочетание для отправки.");
                return;
            }
            if (action.Kind == ActionKind.InsertText)
            {
                if (string.IsNullOrEmpty(action.Text)) errors.Add($"{label}: требуется текст сниппета.");
                return;
            }
            if (string.IsNullOrWhiteSpace(action.Path)) { errors.Add($"{label}: требуется путь."); return; }
            if (action.Kind is ActionKind.OpenFolder or ActionKind.RunProgram or ActionKind.RunScript or ActionKind.CopySelection or ActionKind.MoveSelection)
            {
                if (!action.Path.Contains('{') && !File.Exists(action.Path) && !Directory.Exists(action.Path))
                    warnings.Add($"{label}: путь сейчас недоступен: {action.Path}");
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
            if (key != Keys.None || !Enum.TryParse<Keys>(part, true, out var parsed)
                || !Enum.IsDefined(parsed) || parsed is Keys.None or Keys.Control or Keys.Shift or Keys.Alt)
                return false;
            key = parsed;
        }
        if (key == Keys.None) return false;
        normalized = $"{modifiers}:{(int)key & 0xffff}";
        return true;
    }
}
