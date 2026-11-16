namespace m3nuxa.App.Configuration;

internal static class DefaultConfiguration
{
    internal static AppConfig Create(Func<string, string> text)
    {
        MenuNode Action(string name, ActionKind kind, string value, string icon, string color) => new()
        {
            Name = text(name), Kind = MenuNodeKind.Action, IconPath = Path.Combine("icon-packs", "Boxicons", "bx-" + icon + ".png"), IconColor = color,
            Steps = [kind == ActionKind.SendShortcut
                ? new MenuAction { Kind = kind, Shortcut = value }
                : new MenuAction { Kind = kind, Path = value }]
        };
        MenuNode Group(string name, string icon, string color, params MenuNode[] children) => new()
        {
            Name = text(name), Kind = MenuNodeKind.Group, IconPath = Path.Combine("icon-packs", "Boxicons", "bx-" + icon + ".png"), IconColor = color, Children = [.. children]
        };

        return new AppConfig { Items =
        [
            Group("DefaultFolders", "folder", "#E5A32D",
                Action("Workflow_Desktop", ActionKind.OpenFolder, "{knownFolder:Desktop}", "desktop", "#4E9DEB"),
                Action("Workflow_Documents", ActionKind.OpenFolder, "{knownFolder:Documents}", "file", "#5B8DEF"),
                Action("Workflow_Downloads", ActionKind.OpenFolder, "{knownFolder:Downloads}", "download", "#26A69A"),
                Action("Workflow_Pictures", ActionKind.OpenFolder, "{knownFolder:Pictures}", "image", "#D974B4"),
                Action("Workflow_Music", ActionKind.OpenFolder, "{knownFolder:Music}", "music", "#A477DB"),
                Action("Workflow_Videos", ActionKind.OpenFolder, "{knownFolder:Videos}", "video", "#E57568"),
                Action("Workflow_Profile", ActionKind.OpenFolder, "{knownFolder:Profile}", "user-circle", "#D4993D")),
            Group("DefaultPrograms", "grid-alt", "#4E9DEB",
                Action("DefaultExplorer", ActionKind.RunProgram, "{env:SystemRoot}\\explorer.exe", "folder-open", "#E5A32D"),
                Action("DefaultNotepad", ActionKind.RunProgram, "{env:SystemRoot}\\System32\\notepad.exe", "notepad", "#5B8DEF"),
                Action("DefaultCommandPrompt", ActionKind.RunProgram, "{env:SystemRoot}\\System32\\cmd.exe", "terminal", "#78869C"),
                Action("DefaultTaskManager", ActionKind.RunProgram, "{env:SystemRoot}\\System32\\taskmgr.exe", "task", "#26A69A")),
            Group("DefaultShortcuts", "windows", "#A477DB",
                Action("DefaultTaskView", ActionKind.SendShortcut, "Win+Tab", "grid-alt", "#7C8FE5"),
                Action("DefaultShowDesktop", ActionKind.SendShortcut, "Win+D", "desktop", "#4E9DEB"),
                Action("DefaultSnapLeft", ActionKind.SendShortcut, "Win+Left", "left-arrow-alt", "#26A69A"),
                Action("DefaultSnapRight", ActionKind.SendShortcut, "Win+Right", "right-arrow-alt", "#26A69A"),
                Action("DefaultNewDesktop", ActionKind.SendShortcut, "Ctrl+Win+D", "plus-circle", "#A477DB"),
                Action("DefaultPreviousDesktop", ActionKind.SendShortcut, "Ctrl+Win+Left", "left-arrow-circle", "#A477DB"),
                Action("DefaultNextDesktop", ActionKind.SendShortcut, "Ctrl+Win+Right", "right-arrow-circle", "#A477DB"),
                Action("DefaultCloseDesktop", ActionKind.SendShortcut, "Ctrl+Win+F4", "window-close", "#D66E73"),
                Action("DefaultScreenshot", ActionKind.SendShortcut, "Shift+Win+S", "screenshot", "#D974B4"),
                Action("DefaultClipboardHistory", ActionKind.SendShortcut, "Win+V", "clipboard", "#DB9850"))
        ] };
    }
}
