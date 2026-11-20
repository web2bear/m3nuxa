using System.Text.Json;
using System.Text.Json.Nodes;
using m3nuxa.App;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace m3nuxa.Editor.Views;

public sealed partial class MainPage
{
    private readonly EditorDraft sourceDraft = new();
    private readonly EditorPipeClient sourceClient = new();
    private string? sourcePath;
    private bool sourceSessionReady;
    private bool unsavedNewSource;
    private bool SourceHasUnsavedChanges => unsavedNewSource || sourceDraft.IsDirty;
    private bool configurationActionInProgress;
    private (MenuNode Item, bool FromSource)? paneDrag;

    private void InitializeSourcePane()
    {
        WorkspaceTabs.SelectedItem = PropertiesTab;
        SourceTree.AddHandler(RightTappedEvent, new RightTappedEventHandler((_, args) => SelectRightTappedNode(SourceTree, args)), true);
        SourcePanel.AddHandler(DragOverEvent, new DragEventHandler(MenuTreeDragOver), true);
        SourcePanel.AddHandler(DropEvent, new DragEventHandler(MenuTreeDrop), true);
        SourcePanel.AddHandler(DragLeaveEvent, new DragEventHandler((_, _) => ClearExternalDropIndicator()), true);
        SourceTree.ContextFlyout.Opening += (_, _) => CopySourceItem.IsEnabled =
            draftReady && SourceTree.SelectedNode?.Content is NodeDisplay;
        foreach (var (key, action) in new (VirtualKey, Action)[]
        {
            (VirtualKey.Z, () => RestoreSourceHistory(redo: false)),
            (VirtualKey.Y, () => RestoreSourceHistory(redo: true)),
            (VirtualKey.C, CopySourceSelection)
        })
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = SourceTree };
            accelerator.Invoked += (_, args) => { action(); args.Handled = true; };
            SourceTree.KeyboardAccelerators.Add(accelerator);
        }
    }

    private void WorkspaceTabsChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var showSource = sender.SelectedItem == SourceTab;
        MenuTree.CanDragItems = !showSource;
        MenuTree.CanReorderItems = !showSource;
        foreach (var node in AllTreeNodes(MenuTree.RootNodes))
            if (node.Content is NodeDisplay display) display.CanDrag = showSource;
        DetailsPanel.Visibility = showSource ? Visibility.Collapsed : Visibility.Visible;
        SourcePage.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IEnumerable<TreeViewNode> AllTreeNodes(IEnumerable<TreeViewNode> nodes) =>
        nodes.SelectMany(node => AllTreeNodes(node.Children).Prepend(node));

    private async Task RunConfigurationActionAsync(Func<Task> action)
    {
        if (configurationActionInProgress) return;
        configurationActionInProgress = true;
        IsEnabled = false;
        try { await action(); }
        finally
        {
            IsEnabled = true;
            configurationActionInProgress = false;
        }
    }

    private static bool SameConfiguration(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private async Task<bool> ConfirmLeavingAsync(bool source, bool closing = false)
    {
        var paneDraft = source ? sourceDraft : draftState;
        if (source ? !SourceHasUnsavedChanges : !HasUnsavedChanges)
        {
            if (!paneDraft.HasOnlyUnsavedCopy) return true;
            ShowNotice(L.Get("Editor_KeepTransferredItems"), InfoBarSeverity.Warning);
            return false;
        }
        var profile = source ? sourceDraft.Config : config;
        var pane = L.Get(source ? "Editor_SecondConfiguration" : "Editor_PrimaryConfiguration");
        var answer = await ConfirmAsync(L.Get("Editor_UnsavedChanges_70d744"),
            L.Format(closing ? "Editor_SavePaneBeforeClosing" : "Editor_SavePaneBeforeSwitching", pane, profile.Name),
            L.Get("Editor_Apply_768af6"), L.Get("Editor_Discard_941d9e"));
        if (answer != ContentDialogResult.Secondary)
            return answer == ContentDialogResult.Primary && await (source ? SaveSourceAsync() : ApplyAsync());
        if (!paneDraft.HasOnlyUnsavedCopy) return true;
        ShowNotice(L.Get("Editor_KeepTransferredItems"), InfoBarSeverity.Warning);
        return false;
    }

    private async Task SwitchConfigurationAsync(bool source, string? path)
    {
        if (SameConfiguration(path, source ? sourcePath : draftConfigurationPath)) return;
        if (path is not null && SameConfiguration(path, source ? draftConfigurationPath : sourcePath))
        {
            ShowNotice(L.Get("Editor_DuplicateProfile"), InfoBarSeverity.Warning);
            return;
        }
        if (!await ConfirmLeavingAsync(source)) return;
        if (path is null)
        {
            ClearSource();
            await sourceClient.SendAsync("discard");
            return;
        }
        var response = await (source ? sourceClient : client).SendAsync("loadConfig", new JsonObject { ["path"] = path });
        if (!source) { OpenDraft(response, path); return; }
        OpenSourceDraft(response, path);
    }

    private void OpenSourceDraft(EditorResponse response, string? path = null, bool isNew = false)
    {
        var draft = EditorPipeClient.ReadDraft(response);
        var draftPath = response.Data?["configPath"]?.GetValue<string>() ?? path;
        if (draft is null || string.IsNullOrEmpty(draftPath))
        {
            ShowNotice(response.Message ?? L.Get("Editor_CouldNotReadTheConfiguration_d3f938"), InfoBarSeverity.Error);
            return;
        }
        sourcePath = draftPath;
        sourceSessionReady = true;
        unsavedNewSource = isNew;
        sourceDraft.Reset(draft.Config);
        SourceTree.RootNodes.Clear();
        RefreshSource();
    }

    private void ClearSource()
    {
        sourcePath = null;
        sourceSessionReady = false;
        unsavedNewSource = false;
        sourceDraft.Reset(new AppConfig());
        SourceTree.RootNodes.Clear();
        RefreshSource();
    }

    private void RefreshSource(string? selectId = null)
    {
        if (sourcePath is not null) PopulateTree(SourceTree, sourceDraft.Config, selectId);
        SourceSelector.Content = sourcePath is null ? L.Get("Editor_SelectSource") : sourceDraft.Config.Name;
        SourceImportButton.IsEnabled = sourcePath is not null;
        SourceSaveButton.IsEnabled = sourcePath is not null && SourceHasUnsavedChanges;
    }

    private (List<MenuNode> Items, int Index) SourceInsertion()
    {
        var node = SourceTree.SelectedNode;
        var item = (node?.Content as NodeDisplay)?.Model;
        if (item?.Kind == MenuNodeKind.Group) return (item.Children, 0);
        var siblings = (node?.Parent?.Content as NodeDisplay)?.Model.Children ?? sourceDraft.Config.Items;
        return (siblings, item is not null ? siblings.IndexOf(item) + 1 : node is null ? siblings.Count : 0);
    }

    private void RestoreSourceHistory(bool redo)
        => RestorePaneHistory(source: true, redo);

    private void RestorePaneHistory(bool source, bool redo)
    {
        var primaryId = selected?.Id;
        var sourceId = (SourceTree.SelectedNode?.Content as NodeDisplay)?.Model.Id;
        if (!(source ? sourceDraft : draftState).NavigateHistory(redo, out var linked, out var wouldLoseTransfer))
        {
            if (wouldLoseTransfer) ShowNotice(L.Get("Editor_KeepTransferredItems"), InfoBarSeverity.Warning);
            else if (linked) ShowNotice(L.Get("Editor_TransferHistoryOrder"), InfoBarSeverity.Warning);
            return;
        }
        if (source || linked) RefreshSource(sourceId);
        if (!source || linked)
        {
            ShowConfigurationTitle(draftConfigurationPath);
            MarkDirty();
            RefreshTree(primaryId);
            UpdateConfigurationSelector();
            if (SettingsPage.Visibility == Visibility.Visible) RenderSettings();
        }
    }

    private async void SourceSaveClicked(object sender, RoutedEventArgs args) =>
        await RunConfigurationActionAsync(async () => { await SaveSourceAsync(); });

    private async Task<bool> SaveSourceAsync()
    {
        if (sourcePath is null || !SourceHasUnsavedChanges) return true;
        if (!await ConfirmSavingTransferOriginAsync(source: true)) return false;
        if (activeShortcut is not null) await activeShortcut.StopAsync();
        if (!sourceSessionReady && !await RestoreSourceSessionAsync()) return false;
        var staged = await sourceClient.ReplaceDraftAsync(sourceDraft.Config);
        if (staged.ErrorCode == "draft_not_started")
        {
            sourceSessionReady = false;
            if (!await RestoreSourceSessionAsync()) return false;
            staged = await sourceClient.ReplaceDraftAsync(sourceDraft.Config);
        }
        if (!staged.Success)
        {
            ShowNotice(staged.Message ?? L.Get("Editor_CouldNotPrepareTheConfiguration_896ac2"), InfoBarSeverity.Error);
            return false;
        }
        var response = await sourceClient.SaveProfileAsync(client);
        if (!response.Success)
        {
            ShowNotice(response.Message ?? L.Get("Editor_CouldNotApplyTheConfiguration_9f63ee"), InfoBarSeverity.Error);
            return false;
        }
        if (response.Data?["previousVersion"]?.GetValue<long>() == activeConfigurationVersion &&
            response.Data?["activeVersion"]?.GetValue<long>() is { } version)
            activeConfigurationVersion = version;
        sourceDraft.SetBaseline();
        unsavedNewSource = false;
        SourceSaveButton.IsEnabled = false;
        Notice.IsOpen = false;
        var warnings = response.Data?["Validation"]?["Warnings"]?.Deserialize<string[]>() ?? [];
        if (warnings.Length > 0) ShowNotice(string.Join("\n", warnings), InfoBarSeverity.Warning);
        return true;
    }

    private async Task<bool> RestoreSourceSessionAsync()
    {
        var response = unsavedNewSource
            ? await sourceClient.SendAsync("newConfig", new JsonObject { ["name"] = sourceDraft.Config.Name })
            : await sourceClient.SendAsync("loadConfig", new JsonObject { ["path"] = sourcePath });
        var draft = EditorPipeClient.ReadDraft(response);
        if (draft is null || JsonSerializer.Serialize(draft.Config, JsonOptions) != sourceDraft.Baseline)
        {
            ShowNotice(response.Message ?? L.Get("Editor_SourceChanged"), InfoBarSeverity.Error);
            return false;
        }
        sourcePath = response.Data?["configPath"]?.GetValue<string>() ?? sourcePath;
        sourceSessionReady = true;
        return true;
    }

    private async Task<bool> ConfirmSavingTransferOriginAsync(bool source)
    {
        var paneDraft = source ? sourceDraft : draftState;
        if (paneDraft.HasDiscardedOutgoingTransfer)
        {
            ShowNotice(L.Get("Editor_RestoreDiscardedTransfer"), InfoBarSeverity.Warning);
            return false;
        }
        if (!paneDraft.HasOutgoingTransfer) return true;
        return await ConfirmAsync(L.Get("Editor_Apply_768af6"), L.Get("Editor_SaveTransferOrigin"),
            L.Get("Editor_Apply_768af6"), defaultButton: ContentDialogButton.Close) == ContentDialogResult.Primary;
    }

    private void PaneDragStarting(UIElement sender, DragStartingEventArgs args, bool fromSource)
    {
        if (!draftReady || configurationActionInProgress ||
            sender is not NodeDisplay display)
        {
            args.Cancel = true;
            return;
        }
        paneDrag = (display.Model, fromSource);
        dragEscapeMonitor?.Dispose();
        dragEscapeMonitor = new DragEscapeMonitor(captureRelease: true);
        args.AllowedOperations = DataPackageOperation.Move;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetData("m3nuxa-pane-item", "move");
        dropIndicatorTimer.Start();
    }

    private void PaneDragCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        try
        {
            if (paneDrag is not null && args.DropResult == DataPackageOperation.None &&
                dragEscapeMonitor is { Available: true, Cancelled: false, ReleasePoint: { } point } &&
                EditorOleDropTarget.TryGetClientPosition(App.MainWindow, point, out var x, out var y, requireOverWindow: true))
                CompleteInternalDrop(x, y);
        }
        finally
        {
            paneDrag = null;
            dragEscapeMonitor?.Dispose();
            dragEscapeMonitor = null;
            dropIndicatorTimer.Stop();
            ClearExternalDropIndicator();
        }
    }

    private MenuDropHit SourceTreeHit(double x, double y) =>
        !configurationActionInProgress && draftReady && sourcePath is not null && SourcePage.Visibility == Visibility.Visible
            ? DropGeometry.Menu(x, y, BoundsInWindow(SourceTree), MenuDropRows(SourceTree)) : default;

    private void CopySourceClicked(object sender, RoutedEventArgs args) => CopySourceSelection();

    private void CopySourceSelection()
    {
        if (!draftReady || configurationActionInProgress || SourceTree.SelectedNode?.Content is not NodeDisplay display) return;
        var (items, index) = PrimaryInsertion();
        if (draftState.TryInsertCopy(display.Model, items, index) is not { } clone)
        {
            ShowNotice(L.Get("Editor_CopyTooDeep"), InfoBarSeverity.Warning);
            return;
        }
        MarkDirty();
        RefreshTree(clone.Id);
    }

    private bool MoveBetweenPanes(MenuNode item, (List<MenuNode> Items, int Index) target, bool fromSource)
    {
        var origin = fromSource ? sourceDraft : draftState;
        var destination = fromSource ? draftState : sourceDraft;
        if (origin.TryTransfer(destination, item.Id, target.Items, target.Index, out var conflictingHotkey) is not { } moved)
        {
            var message = origin.MustSaveBeforeMoving(item) ? L.Get("Editor_SaveBeforeMovingAgain") :
                conflictingHotkey is null ? L.Get("Editor_CopyTooDeep") : L.Format("Editor_TransferShortcutConflict", conflictingHotkey);
            ShowNotice(message, InfoBarSeverity.Warning);
            return false;
        }
        MarkDirty();
        RefreshTree(fromSource ? moved.Id : null);
        RefreshSource(fromSource ? null : moved.Id);
        ShowNotice(L.Get("Editor_TransferDrafts"), InfoBarSeverity.Informational);
        return true;
    }
}
