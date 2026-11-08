using System.Text.Json.Serialization;

namespace m3nuxa.App;

public enum MenuNodeKind { Group, Action, Separator }
public enum ActionKind { OpenFolder, RunProgram, RunScript, SendShortcut, CopySelection, MoveSelection, InsertText, OpenItem, Delay, SetClipboardText, PasteText }

public sealed class MenuAction
{
    public string Name { get; set; } = "";
    public ActionKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public string Text { get; set; } = "";
    public string Interpreter { get; set; } = "";
    public bool HideConsole { get; set; }
    public int DelayMilliseconds { get; set; }
    public bool WaitForExit { get; set; }
    public int? TimeoutMilliseconds { get; set; }
    public bool RestoreClipboard { get; set; } = true;
}

public sealed class MenuNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string IconColor { get; set; } = "";
    public string Hotkey { get; set; } = "";
    public MenuNodeKind Kind { get; set; }
    public List<MenuAction> Steps { get; set; } = [];

    // Compatibility for existing code. New consumers should edit Steps directly.
    [JsonIgnore]
    public MenuAction? Action
    {
        get => Steps?.FirstOrDefault();
        set
        {
            if (value is null) { Steps = []; return; }
            Steps ??= [];
            if (Steps.Count == 0) Steps.Add(value);
            else Steps[0] = value;
        }
    }

    // Schema 1 stores one action. Never write the legacy property in schema 2.
    [JsonPropertyName("Action"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MenuAction? LegacyAction { get => null; set { if (value is not null && (Steps is null || Steps.Count == 0)) Action = value; } }
    public List<MenuNode> Children { get; set; } = [];
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 2;
    [JsonIgnore]
    public string MainHotkey { get; set; } = "Ctrl+Alt+Space";
    // Read old configurations once; new menu files contain only the tree.
    [JsonPropertyName("MainHotkey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyMainHotkey { get => null; set { if (value is not null) MainHotkey = value; } }
    public List<MenuNode> Items { get; set; } = [];
}

public sealed class ConfigDraft
{
    public required AppConfig Config { get; set; }
    public required long BaseVersion { get; set; }
}

public sealed record ValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record ApplyResult(bool Success, string? Error, ValidationResult Validation)
{
    public static ApplyResult Failed(string error, ValidationResult? validation = null) =>
        new(false, error, validation ?? new ValidationResult([], []));
}

public sealed record ReloadResult(bool Success, string? Error);
