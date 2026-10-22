using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace m3nuxa.App;

public sealed class EditorForm : Form
{
    private readonly ConfigStore store;
    private ConfigDraft draft;
    private readonly Action? applied;
    private readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true };
    private readonly TableLayoutPanel fields = new() { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(20) };
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private readonly TextBox mainHotkey = new() { Width = 160 };
    private MenuNode? selected;
    private bool loading;
    private bool dirty;

    public EditorForm(ConfigStore store, Action? applied = null)
    {
        this.store = store;
        this.applied = applied;
        draft = store.CreateDraft();
        Text = "m3nuxa — редактор меню";
        Width = 1120; Height = 760; MinimumSize = new Size(800, 550);
        Font = new Font("Segoe UI Variable Text", 10);
        var menu = new MenuStrip { Dock = DockStyle.Top };
        var fileMenu = new ToolStripMenuItem("Файл");
        ((ToolStripMenuItem)fileMenu.DropDownItems.Add("Применить", null, (_, _) => Apply())).ShortcutKeys = Keys.Control | Keys.S;
        fileMenu.DropDownItems.Add("Загрузить конфигурацию…", null, (_, _) => LoadFile());
        fileMenu.DropDownItems.Add("Перезагрузить с диска", null, (_, _) => Reload());
        fileMenu.DropDownItems.Add("Сохранить черновик отдельно…", null, (_, _) => SaveDraft());
        var editMenu = new ToolStripMenuItem("Правка");
        ((ToolStripMenuItem)editMenu.DropDownItems.Add("Отменить", null, (_, _) => Undo())).ShortcutKeys = Keys.Control | Keys.Z;
        ((ToolStripMenuItem)editMenu.DropDownItems.Add("Повторить", null, (_, _) => Redo())).ShortcutKeys = Keys.Control | Keys.Y;
        menu.Items.Add(fileMenu); menu.Items.Add(editMenu);
        MainMenuStrip = menu;

        var addArea = new Panel { Dock = DockStyle.Top, Height = 116, Padding = new Padding(16, 12, 12, 4) };
        var addTitle = new Label { Text = "Добавить", Dock = DockStyle.Top, Height = 28, Font = new Font(Font, FontStyle.Bold) };
        var addButtons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, WrapContents = false, Margin = Padding.Empty };
        AddIconButton(addButtons, "\uE8B7", "Папку", AddFolder);
        AddIconButton(addButtons, "\uE8A5", "Файл или скрипт", AddFiles);
        AddIconButton(addButtons, "\uE8D2", "Сниппет", AddSnippet);
        AddIconButton(addButtons, "\uE902", "Группу", () => AddNode(MenuNodeKind.Group));
        AddIconButton(addButtons, "\uE71B", "Разделитель", () => AddNode(MenuNodeKind.Separator));
        addArea.Controls.Add(addButtons); addArea.Controls.Add(addTitle);
        var addHint = new Label
        {
            Dock = DockStyle.Bottom, Height = 24, AutoEllipsis = true,
            Text = IsElevated() ? "Перетаскивание из Проводника требует запуска без прав администратора." : "Перетащите файлы и папки из Проводника в дерево."
        };
        addArea.Controls.Add(addHint);

        var treeMenu = new ContextMenuStrip();
        treeMenu.Items.Add("Дублировать", null, (_, _) => Duplicate());
        treeMenu.Items.Add("Удалить", null, (_, _) => Delete());
        treeMenu.Opening += (_, e) => { if (tree.SelectedNode is null) e.Cancel = true; };
        tree.ContextMenuStrip = treeMenu;
        tree.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) tree.SelectedNode = e.Node; };

        var hotkeyArea = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(16, 10, 8, 4), WrapContents = false };
        hotkeyArea.Controls.Add(new Label { Text = "Вызов меню", AutoSize = true, Padding = new Padding(0, 5, 10, 0) });
        hotkeyArea.Controls.Add(mainHotkey);
        mainHotkey.TextChanged += (_, _) => { if (!loading) { Remember(); draft.Config.MainHotkey = mainHotkey.Text; MarkDirty(); } };
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 400 };
        split.Panel1.Controls.Add(tree); split.Panel1.Controls.Add(hotkeyArea); split.Panel1.Controls.Add(addArea);
        var propertiesTitle = new Label { Text = "Свойства пункта", Dock = DockStyle.Top, Height = 56, Padding = new Padding(20, 17, 0, 0), Font = new Font(Font.FontFamily, 15, FontStyle.Bold) };
        split.Panel2.Controls.Add(fields); split.Panel2.Controls.Add(propertiesTitle);
        Controls.Add(split); Controls.Add(menu);
        tree.AfterSelect += (_, _) => ShowProperties();
        tree.ItemDrag += (_, e) => tree.DoDragDrop(e.Item!, DragDropEffects.Move);
        tree.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : e.Data?.GetDataPresent(typeof(TreeNode)) == true ? DragDropEffects.Move : DragDropEffects.None;
        tree.DragOver += TreeDragOver;
        tree.DragLeave += (_, _) => ClearInsertMark();
        tree.DragDrop += TreeDragDrop;
        FormClosing += OnEditorClosing;
        RefreshTree();
    }

    private static void AddIconButton(FlowLayoutPanel panel, string glyph, string name, Action action)
    {
        var button = new Button { Text = glyph, Width = 40, Height = 38, Margin = new Padding(0, 0, 8, 0), Font = new Font("Segoe Fluent Icons", 16), AccessibleName = name };
        new ToolTip().SetToolTip(button, name);
        button.Click += (_, _) => action(); panel.Controls.Add(button);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Выберите папку для пункта меню" };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths([dialog.SelectedPath]);
    }

    private void AddFiles()
    {
        using var dialog = new OpenFileDialog { Title = "Добавить файл или скрипт", Multiselect = true,
            Filter = "Программы, скрипты и ярлыки|*.exe;*.bat;*.cmd;*.ps1;*.lnk|Все файлы|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames);
    }

    private void AddSnippet()
    {
        Remember();
        var item = new MenuNode { Kind = MenuNodeKind.Action, Name = "Новый сниппет", Action = new MenuAction { Kind = ActionKind.InsertText } };
        var parent = selected?.Kind == MenuNodeKind.Group ? selected : tree.SelectedNode?.Parent is null ? null : Find((string?)tree.SelectedNode.Parent.Tag);
        (parent?.Children ?? draft.Config.Items).Add(item);
        MarkDirty(); RefreshTree(item.Id);
    }

    private void AddPaths(IEnumerable<string> paths, List<MenuNode>? destination = null, int index = -1)
    {
        destination ??= selected?.Kind == MenuNodeKind.Group ? selected.Children : Siblings(tree.SelectedNode);
        if (index < 0 || index > destination.Count) index = destination.Count;
        var errors = new List<string>();
        string? lastId = null;
        foreach (var path in paths)
        {
            try
            {
                var item = Import(path);
                if (lastId is null) Remember();
                destination.Insert(index++, item);
                lastId = item.Id;
            }
            catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); }
        }
        if (lastId is not null) { MarkDirty(); RefreshTree(lastId); }
        if (errors.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Не удалось добавить некоторые объекты", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private string Snapshot() => JsonSerializer.Serialize(draft.Config);
    private void Remember() { if (loading) return; undo.Push(Snapshot()); redo.Clear(); }
    private void MarkDirty() { dirty = true; Text = "m3nuxa — редактор меню *"; }
    private void Restore(string json)
    {
        loading = true;
        draft.Config = JsonSerializer.Deserialize<AppConfig>(json)!;
        loading = false; MarkDirty(); RefreshTree();
    }
    private void Undo() { if (undo.Count == 0) return; redo.Push(Snapshot()); Restore(undo.Pop()); }
    private void Redo() { if (redo.Count == 0) return; undo.Push(Snapshot()); Restore(redo.Pop()); }

    private void RefreshTree(string? selectId = null)
    {
        loading = true; tree.BeginUpdate(); tree.Nodes.Clear();
        foreach (var item in draft.Config.Items) tree.Nodes.Add(Build(item));
        tree.ExpandAll(); tree.EndUpdate();
        mainHotkey.Text = draft.Config.MainHotkey;
        tree.SelectedNode = FindTreeNode(tree.Nodes, selectId);
        loading = false; ShowProperties();
    }
    private static TreeNode Build(MenuNode item)
    {
        var node = new TreeNode(item.Kind == MenuNodeKind.Separator ? "────────" : string.IsNullOrWhiteSpace(item.Name) ? "(без имени)" : item.Name) { Tag = item.Id };
        foreach (var child in item.Children) node.Nodes.Add(Build(child));
        return node;
    }
    private static TreeNode? FindTreeNode(TreeNodeCollection nodes, string? id)
    {
        if (id is null) return null;
        foreach (TreeNode node in nodes) { if ((string?)node.Tag == id) return node; var found = FindTreeNode(node.Nodes, id); if (found != null) return found; }
        return null;
    }
    private MenuNode? Find(string? id) => Find(draft.Config.Items, id);
    private static MenuNode? Find(IEnumerable<MenuNode> nodes, string? id)
    {
        foreach (var node in nodes) { if (node.Id == id) return node; var child = Find(node.Children, id); if (child != null) return child; }
        return null;
    }
    private List<MenuNode> Siblings(TreeNode? node)
    {
        var parent = node?.Parent is null ? null : Find((string?)node.Parent.Tag);
        return parent?.Children ?? draft.Config.Items;
    }
    private void AddNode(MenuNodeKind kind)
    {
        Remember();
        var item = new MenuNode { Kind = kind, Name = kind == MenuNodeKind.Group ? "Новая группа" : kind == MenuNodeKind.Action ? "Новое действие" : "", Action = kind == MenuNodeKind.Action ? new MenuAction() : null };
        var parent = selected?.Kind == MenuNodeKind.Group ? selected : tree.SelectedNode?.Parent is null ? null : Find((string?)tree.SelectedNode.Parent.Tag);
        (parent?.Children ?? draft.Config.Items).Add(item); MarkDirty(); RefreshTree(item.Id);
    }
    private void Duplicate()
    {
        if (selected is null) return; Remember();
        var clone = JsonSerializer.Deserialize<MenuNode>(JsonSerializer.Serialize(selected))!;
        NewIds(clone); clone.Name += " (копия)";
        var siblings = Siblings(tree.SelectedNode); siblings.Insert(siblings.IndexOf(selected) + 1, clone);
        MarkDirty(); RefreshTree(clone.Id);
    }
    private static void NewIds(MenuNode node) { node.Id = Guid.NewGuid().ToString("N"); foreach (var child in node.Children) NewIds(child); }
    private void Delete()
    {
        if (selected is null) return; Remember(); Siblings(tree.SelectedNode).Remove(selected); MarkDirty(); RefreshTree();
    }

    private void ShowProperties()
    {
        selected = Find((string?)tree.SelectedNode?.Tag);
        fields.SuspendLayout(); fields.Controls.Clear(); fields.RowStyles.Clear(); fields.RowCount = 0;
        if (selected is { Kind: not MenuNodeKind.Separator } item)
        {
            AddField("Имя", item.Name, value => item.Name = value);
            AddField("Горячая клавиша", item.Hotkey, value => item.Hotkey = value);
            AddIconField(item);
            if (item.Kind == MenuNodeKind.Action)
            {
                item.Action ??= new MenuAction();
                var action = item.Action;
                var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
                kind.Items.AddRange(Enum.GetValues<ActionKind>().Cast<object>().ToArray());
                kind.SelectedItem = action.Kind;
                kind.SelectedIndexChanged += (_, _) =>
                {
                    if (loading || kind.SelectedItem is not ActionKind chosen || chosen == action.Kind) return;
                    Remember(); action.Kind = chosen; MarkDirty(); ShowProperties();
                };
                AddControl("Тип действия", kind);
                if (action.Kind is ActionKind.OpenFolder or ActionKind.RunProgram or ActionKind.RunScript or ActionKind.CopySelection or ActionKind.MoveSelection)
                    AddField(action.Kind is ActionKind.CopySelection or ActionKind.MoveSelection ? "Папка назначения" : "Путь", action.Path, value => action.Path = value, true);
                if (action.Kind is ActionKind.RunProgram or ActionKind.RunScript)
                {
                    AddField("Аргументы", action.Arguments, value => action.Arguments = value, true);
                    AddField("Рабочая папка", action.WorkingDirectory, value => action.WorkingDirectory = value, true);
                }
                if (action.Kind == ActionKind.RunScript)
                {
                    AddField("Интерпретатор", action.Interpreter, value => action.Interpreter = value);
                    var hide = new CheckBox { Text = "Скрыть консоль", Checked = action.HideConsole, AutoSize = true };
                    hide.CheckedChanged += (_, _) => { Remember(); action.HideConsole = hide.Checked; MarkDirty(); };
                    AddControl("Консоль", hide);
                }
                if (action.Kind == ActionKind.SendShortcut) AddField("Отправить сочетание", action.Shortcut, value => action.Shortcut = value);
                if (action.Kind == ActionKind.InsertText)
                {
                    var textBox = new TextBox { Text = action.Text, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 180, Width = 430, AcceptsReturn = true };
                    textBox.Enter += (_, _) => Remember();
                    textBox.TextChanged += (_, _) => { if (loading) return; action.Text = textBox.Text; MarkDirty(); };
                    AddControl("Текст", textBox);
                }
            }
        }
        fields.ResumeLayout();
    }
    private void AddControl(string label, Control control)
    {
        var row = fields.RowCount++;
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 7, 6, 0) }, 0, row);
        fields.Controls.Add(control, 1, row);
    }
    private void AddField(string label, string value, Action<string> set, bool variables = false)
    {
        var box = new TextBox { Text = value, Width = 330, Anchor = AnchorStyles.Left | AnchorStyles.Right };
        box.Enter += (_, _) => Remember();
        box.TextChanged += (_, _) => { if (loading) return; set(box.Text); MarkDirty(); if (label == "Имя" && tree.SelectedNode != null) tree.SelectedNode.Text = box.Text; };
        if (!variables) { AddControl(label, box); return; }
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        panel.Controls.Add(box);
        var vars = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        vars.Items.AddRange(["Переменная…", "{selectedPaths}", "{selectedPath}", "{currentFolder}", "{selectedText}", "{fileName}", "{extension}", "{parentFolder}", "{urlEncodedText}"]);
        vars.SelectedIndex = 0;
        vars.SelectedIndexChanged += (_, _) => { if (vars.SelectedIndex > 0) { box.SelectedText = vars.SelectedItem!.ToString(); vars.SelectedIndex = 0; } };
        panel.Controls.Add(vars); AddControl(label, panel);
    }
    private void AddIconField(MenuNode item)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var preview = new PictureBox { Width = 32, Height = 32, SizeMode = PictureBoxSizeMode.Zoom };
        try
        {
            if (File.Exists(item.IconPath))
                preview.Image = Path.GetExtension(item.IconPath).ToLowerInvariant() switch
                {
                    ".ico" => new Icon(item.IconPath).ToBitmap(),
                    ".exe" or ".dll" => Icon.ExtractAssociatedIcon(item.IconPath)?.ToBitmap(),
                    _ => new Bitmap(item.IconPath)
                };
        }
        catch { }
        var button = new Button { Text = "Выбрать…", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Иконки и изображения|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.dll" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var path = dialog.FileName;
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    var resources = Path.Combine(AppContext.BaseDirectory, "resources"); Directory.CreateDirectory(resources);
                    var destination = Path.Combine(resources, Guid.NewGuid().ToString("N") + Path.GetExtension(path));
                    File.Copy(path, destination); path = destination;
                }
                Remember(); item.IconPath = path; MarkDirty(); ShowProperties();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Иконка", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        panel.Controls.Add(preview); panel.Controls.Add(button); AddControl("Иконка", panel);
    }

    private void TreeDragDrop(object? sender, DragEventArgs e)
    {
        var point = tree.PointToClient(new Point(e.X, e.Y));
        var target = tree.GetNodeAt(point);
        var placement = GetPlacement(target, point.Y);
        ClearInsertMark();
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop); if (files is null) return;
            var siblings = placement.Parent?.Children ?? draft.Config.Items;
            var index = InsertionIndex(siblings, placement.Anchor, placement.After);
            AddPaths(files, siblings, index);
            return;
        }
        if (e.Data?.GetData(typeof(TreeNode)) is not TreeNode source) return;
        var itemToMove = Find((string?)source.Tag); if (itemToMove is null) return;
        var parentNode = placement.Anchor is null && target is not null ? target : target?.Parent;
        for (var current = parentNode; current != null; current = current.Parent) if (current == source) return;
        if (target == source) return;
        Remember(); Siblings(source).Remove(itemToMove);
        var siblings2 = placement.Parent?.Children ?? draft.Config.Items;
        siblings2.Insert(InsertionIndex(siblings2, placement.Anchor, placement.After), itemToMove);
        MarkDirty(); RefreshTree(itemToMove.Id);
    }

    private void TreeDragOver(object? sender, DragEventArgs e)
    {
        var point = tree.PointToClient(new Point(e.X, e.Y));
        var target = tree.GetNodeAt(point);
        var placement = GetPlacement(target, point.Y);
        if (e.Data?.GetData(typeof(TreeNode)) is TreeNode source)
        {
            var parent = placement.Anchor is null && target is not null ? target : target?.Parent;
            if (target == source || AncestorIs(parent, source)) { e.Effect = DragDropEffects.None; ClearInsertMark(); return; }
            e.Effect = DragDropEffects.Move;
        }
        else e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        if (placement.Anchor is null)
        {
            ClearInsertMark();
            tree.SelectedNode = target; // A highlighted group means insertion inside it.
        }
        else if (target is not null)
            SetInsertMark(target, placement.After);
    }

    private static bool AncestorIs(TreeNode? node, TreeNode ancestor)
    {
        for (; node is not null; node = node.Parent) if (node == ancestor) return true;
        return false;
    }

    private (MenuNode? Parent, MenuNode? Anchor, bool After) GetPlacement(TreeNode? target, int y)
    {
        if (target is null) return (null, null, true);
        var item = Find((string?)target.Tag);
        var relativeY = y - target.Bounds.Top;
        if (item?.Kind == MenuNodeKind.Group && relativeY >= target.Bounds.Height / 3 && relativeY < target.Bounds.Height * 2 / 3)
            return (item, null, true);
        return (Find((string?)target.Parent?.Tag), item, relativeY >= target.Bounds.Height / 2);
    }

    private static int InsertionIndex(List<MenuNode> siblings, MenuNode? anchor, bool after)
    {
        if (anchor is null) return siblings.Count;
        var index = siblings.FindIndex(node => node.Id == anchor.Id);
        return index < 0 ? siblings.Count : index + (after ? 1 : 0);
    }

    private void SetInsertMark(TreeNode target, bool after) => SendMessage(tree.Handle, 0x111A, after ? 1 : 0, target.Handle);
    private void ClearInsertMark() => SendMessage(tree.Handle, 0x111A, 0, 0);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    private static MenuNode Import(string path)
    {
        var directory = Directory.Exists(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!directory && extension is not (".exe" or ".bat" or ".cmd" or ".ps1" or ".lnk")) throw new InvalidOperationException("Тип файла не поддерживается");
        var action = new MenuAction { Kind = directory ? ActionKind.OpenFolder : extension is ".bat" or ".cmd" or ".ps1" ? ActionKind.RunScript : ActionKind.RunProgram, Path = path };
        if (extension == ".ps1") action.Interpreter = ResolvePowerShell();
        if (extension == ".lnk")
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host недоступен");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);
            action.Path = (string)link.TargetPath; action.Arguments = (string)link.Arguments; action.WorkingDirectory = (string)link.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(action.Path)) throw new InvalidOperationException("У ярлыка нет цели");
            var targetExt = Path.GetExtension(action.Path).ToLowerInvariant();
            action.Kind = Directory.Exists(action.Path) ? ActionKind.OpenFolder : targetExt is ".bat" or ".cmd" or ".ps1" ? ActionKind.RunScript : ActionKind.RunProgram;
            if (targetExt == ".ps1") action.Interpreter = ResolvePowerShell();
        }
        return new MenuNode { Kind = MenuNodeKind.Action, Name = Path.GetFileNameWithoutExtension(path), Action = action, IconPath = directory ? "" : action.Path };
    }
    private static string ResolvePowerShell()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (File.Exists(Path.Combine(directory, "pwsh.exe"))) return "pwsh.exe";
        return "powershell.exe";
    }
    private void Apply()
    {
        var result = store.Apply(draft);
        if (!result.Success)
        {
            if (result.Error?.Contains("version", StringComparison.OrdinalIgnoreCase) == true || result.Error?.Contains("устар", StringComparison.OrdinalIgnoreCase) == true)
            {
                var choice = MessageBox.Show(this, "Черновик устарел. Да — сохранить его отдельно, Нет — загрузить текущую конфигурацию.", "Конфликт версий", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (choice == DialogResult.Yes) SaveDraft(); else if (choice == DialogResult.No) Reload();
            }
            else MessageBox.Show(this, result.Error + Environment.NewLine + string.Join(Environment.NewLine, result.Validation.Errors), "Не удалось применить", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        draft = store.CreateDraft(); undo.Clear(); redo.Clear(); dirty = false; Text = "m3nuxa — редактор меню"; applied?.Invoke();
        if (result.Validation.Warnings.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, result.Validation.Warnings), "Предупреждения", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    private void SaveDraft()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "m3nuxa-draft.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { store.SaveDraft(draft, dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить черновик", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private void Reload()
    {
        if (dirty && MessageBox.Show(this, "Отбросить несохранённые правки?", "Перезагрузка", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var result = store.Reload();
        if (!result.Success) { MessageBox.Show(this, result.Error, "Ошибка загрузки", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        draft = store.CreateDraft(); undo.Clear(); redo.Clear(); dirty = false; Text = "m3nuxa — редактор меню"; RefreshTree(); applied?.Invoke();
    }
    private void LoadFile()
    {
        if (dirty && MessageBox.Show(this, "Отбросить несохранённые правки?", "Загрузка", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        using var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { draft = store.LoadDraft(dialog.FileName); undo.Clear(); redo.Clear(); MarkDirty(); RefreshTree(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка загрузки", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private void OnEditorClosing(object? sender, FormClosingEventArgs e)
    {
        if (!dirty) return;
        var answer = MessageBox.Show(this, "Применить изменения перед закрытием?", "Несохранённые правки", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) e.Cancel = true;
        else if (answer == DialogResult.Yes) { Apply(); e.Cancel = dirty; }
    }
}
