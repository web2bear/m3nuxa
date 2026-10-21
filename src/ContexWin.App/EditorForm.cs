using System.Runtime.InteropServices;
using System.Text.Json;

namespace ContexWin.App;

public sealed class EditorForm : Form
{
    private readonly ConfigStore store;
    private ConfigDraft draft;
    private readonly Action? applied;
    private readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true };
    private readonly TableLayoutPanel fields = new() { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 2, Padding = new Padding(12) };
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
        Text = "ContexWin — редактор меню";
        Width = 1050; Height = 700; MinimumSize = new Size(740, 470);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, AutoSize = false, WrapContents = false };
        AddButton(toolbar, "Группа", () => AddNode(MenuNodeKind.Group));
        AddButton(toolbar, "Действие", () => AddNode(MenuNodeKind.Action));
        AddButton(toolbar, "Разделитель", () => AddNode(MenuNodeKind.Separator));
        AddButton(toolbar, "Дублировать", Duplicate);
        AddButton(toolbar, "Удалить", Delete);
        AddButton(toolbar, "Отменить", Undo);
        AddButton(toolbar, "Повторить", Redo);
        AddButton(toolbar, "Применить", Apply);
        AddButton(toolbar, "Загрузить файл", LoadFile);
        AddButton(toolbar, "Перезагрузить", Reload);
        toolbar.Controls.Add(new Label { Text = "Вызов меню:", AutoSize = true, Padding = new Padding(8, 9, 0, 0) });
        toolbar.Controls.Add(mainHotkey);
        mainHotkey.TextChanged += (_, _) => { if (!loading) { Remember(); draft.Config.MainHotkey = mainHotkey.Text; MarkDirty(); } };
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 380 };
        split.Panel1.Controls.Add(tree);
        split.Panel2.Controls.Add(fields);
        Controls.Add(split); Controls.Add(toolbar);
        tree.AfterSelect += (_, _) => ShowProperties();
        tree.ItemDrag += (_, e) => tree.DoDragDrop(e.Item!, DragDropEffects.Move);
        tree.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : e.Data?.GetDataPresent(typeof(TreeNode)) == true ? DragDropEffects.Move : DragDropEffects.None;
        tree.DragOver += TreeDragOver;
        tree.DragLeave += (_, _) => ClearInsertMark();
        tree.DragDrop += TreeDragDrop;
        FormClosing += OnEditorClosing;
        RefreshTree();
    }

    private static void AddButton(FlowLayoutPanel panel, string name, Action action)
    {
        var button = new Button { Text = name, AutoSize = true, Height = 30 };
        button.Click += (_, _) => action(); panel.Controls.Add(button);
    }

    private string Snapshot() => JsonSerializer.Serialize(draft.Config);
    private void Remember() { if (loading) return; undo.Push(Snapshot()); redo.Clear(); }
    private void MarkDirty() { dirty = true; Text = "ContexWin — редактор меню *"; }
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
                var kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, DataSource = Enum.GetValues<ActionKind>() };
                kind.SelectedItem = action.Kind;
                kind.SelectedValueChanged += (_, _) => { Remember(); action.Kind = (ActionKind)kind.SelectedItem!; MarkDirty(); ShowProperties(); };
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
            Remember(); var errors = new List<string>(); string? lastId = null;
            var siblings = placement.Parent?.Children ?? draft.Config.Items;
            var index = InsertionIndex(siblings, placement.Anchor, placement.After);
            foreach (var file in files)
            {
                try { var item = Import(file); siblings.Insert(index++, item); lastId = item.Id; }
                catch (Exception ex) { errors.Add($"{file}: {ex.Message}"); }
            }
            if (lastId != null) { MarkDirty(); RefreshTree(lastId); }
            if (errors.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Не удалось добавить некоторые объекты", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        draft = store.CreateDraft(); undo.Clear(); redo.Clear(); dirty = false; Text = "ContexWin — редактор меню"; applied?.Invoke();
        if (result.Validation.Warnings.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, result.Validation.Warnings), "Предупреждения", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    private void SaveDraft()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "contexwin-draft.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { store.SaveDraft(draft, dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось сохранить черновик", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private void Reload()
    {
        if (dirty && MessageBox.Show(this, "Отбросить несохранённые правки?", "Перезагрузка", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var result = store.Reload();
        if (!result.Success) { MessageBox.Show(this, result.Error, "Ошибка загрузки", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        draft = store.CreateDraft(); undo.Clear(); redo.Clear(); dirty = false; Text = "ContexWin — редактор меню"; RefreshTree(); applied?.Invoke();
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
