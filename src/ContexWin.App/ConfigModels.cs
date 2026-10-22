using System.Text.Json.Serialization;

namespace ContexWin.App;

public enum MenuNodeKind { Group, Action, Separator }
public enum ActionKind { OpenFolder, RunProgram, RunScript, SendShortcut, CopySelection, MoveSelection, InsertText }

public sealed class MenuAction
{
    public ActionKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Shortcut { get; set; } = "";
    public string Text { get; set; } = "";
    public string Interpreter { get; set; } = "";
    public bool HideConsole { get; set; }
}

public sealed class MenuNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string IconPath { get; set; } = "";
    public string Hotkey { get; set; } = "";
    public MenuNodeKind Kind { get; set; }
    public MenuAction? Action { get; set; }
    public List<MenuNode> Children { get; set; } = [];
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;
    public string MainHotkey { get; set; } = "Ctrl+Alt+Space";
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
