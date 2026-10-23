using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ProfilesPage : NativePage, IPageBackNavigation
{
    private const int MaximumConfigBytes = 4 * 1024 * 1024;
    private readonly TextBlock fileName = Ui.Text("—", 24, true);
    private readonly TextBlock path = Ui.Muted("");
    private readonly TextBlock counts = Ui.Muted("");
    private readonly TextBox importUrl = Ui.SearchBox("订阅地址", "ConfigImportUrlInput");
    private readonly ConfigCodeEditor code = new();
    private TextBox Editor => code.Editor;
    private readonly TextBlock editorPath = Ui.Muted("");
    private readonly TextBlock editorState = Ui.Muted("").Id("ConfigEditorState");
    private readonly TextBlock editorNotice = Ui.Muted("").Id("ConfigEditorNotice");
    private readonly TextBlock editorPosition = Ui.Muted("").Id("ConfigEditorPosition");
    private readonly Grid editPane = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel overview = new();
    private readonly ScrollViewer overviewScroll;
    private readonly Grid editorFooter = new();
    private readonly Border confirmationPane;
    private readonly TextBlock confirmationTitle = Ui.Text("", 14, true);
    private readonly TextBlock confirmationText = Ui.Muted("");
    private readonly Button keepEditing;
    private readonly Button confirmDiscard;
    private readonly Button openEditor;
    private readonly Button reload;
    private readonly Button importButton;
    private readonly Button importUrlButton;
    private readonly Button readAgain;
    private readonly Button save;
    private readonly Button saveOnly;
    private readonly Button discard;
    private readonly Button closeEditor;
    private readonly ResourceUpdatePanel resources;
    private readonly Border resourcesTarget;
    private string revision = "", originalContent = "", documentPath = "", problem = "", lastPhase = "";
    private bool applyingDocument, loaded, reading, saving, reloading, importing, dirty, conflict, saved, reloadPending, updateWasActive;
    private bool writable = true;
    private long documentGeneration, draftGeneration, summaryReadAt;
    private int? groupCount, ruleCount;
    private int maxBytes = MaximumConfigBytes;
    private int editorBytes;
    private Task? readTask;
    private Confirmation confirmation;
    private enum Confirmation { None, Close, Read }
    private bool Busy => reading || saving || importing;
    public bool CanGoBack => editPane.Visibility == Visibility.Visible;
    public event Action? BackStateChanged;

    public ProfilesPage(AppSession session) : base(session, "profiles", "配置", "管理当前 YAML，导入订阅并更新规则与 Geo 数据")
    {
        importButton = IconAction("导入配置", "ImportConfig", "arrow-import", ChooseFile, true); Tools.Children.Add(importButton);
        openEditor = IconAction("编辑配置", "EditConfig", "edit", OpenEditor);
        reload = IconAction("重新加载", "ReloadConfig", "update", Reload);
        var more = FluentControls.IconButton("more", "更多配置操作", "ConfigMore"); var menu = new ContextMenu();
        AddDirectory("打开配置文件夹", "OpenConfigFolder", "config"); AddDirectory("打开日志文件夹", "OpenLogsFolder", "logs");
        more.ContextMenu = menu; more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
        void AddDirectory(string title, string id, string kind)
        {
            var item = L.Bind(new MenuItem().Id(id), HeaderedItemsControl.HeaderProperty, title);
            item.Click += async (_, _) => await RunAsync(() => Command("desktop_open_directory", new { kind })); menu.Items.Add(item);
        }
        var current = new Grid(); current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); current.ColumnDefinitions.Add(new ColumnDefinition());
        var iconWell = new Border { Child = new FluentIcon("document-text", 26), Padding = new Thickness(18), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        iconWell.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush"); current.Children.Add(iconWell);
        path.TextWrapping = TextWrapping.NoWrap; path.TextTrimming = TextTrimming.CharacterEllipsis; path.Margin = new Thickness(0, 6, 0, 0); path.Id("CurrentConfigPath");
        fileName.TextWrapping = TextWrapping.NoWrap; fileName.TextTrimming = TextTrimming.CharacterEllipsis; fileName.Id("CurrentConfigName"); counts.Margin = new Thickness(0, 10, 0, 0);
        var identity = Ui.Column(Ui.Hint("当前配置"), fileName, path, counts); Grid.SetColumn(identity, 1); current.Children.Add(identity);
        var fileActions = Ui.Row(openEditor, reload, more); fileActions.Margin = new Thickness(0, 20, 0, 0); overview.Children.Add(Ui.Card(Ui.Column(current, fileActions), 24));
        var importHeading = Ui.Row(Ui.Label("导入配置", 18, true), Ui.Hint("也可以将 YAML 文件拖入窗口")); importHeading.Margin = new Thickness(0, 12, 0, 4); overview.Children.Add(importHeading);
        overview.Children.Add(ImportRow("file", "从文件导入", "选择本机 .yaml 或 .yml 文件，原配置会自动备份。", IconAction("选择文件", "ImportConfigFile", "folder-open", ChooseFile)));
        importUrl.MinWidth = 100; importUrl.HorizontalAlignment = HorizontalAlignment.Stretch; importUrl.Margin = new Thickness(0, 0, 10, 0);
        importUrlButton = IconAction("导入地址", "ImportConfigUrl", "arrow-import", ImportUrl); importUrlButton.Margin = new Thickness(0);
        var urlRow = new Grid { Margin = new Thickness(0, 12, 0, 0) }; urlRow.ColumnDefinitions.Add(new ColumnDefinition()); urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        urlRow.Children.Add(importUrl); Grid.SetColumn(importUrlButton, 1); urlRow.Children.Add(importUrlButton);
        overview.Children.Add(ImportRow("link", "从 URL 导入", "输入 HTTP 或 HTTPS 订阅地址。", null, urlRow));
        overview.Children.Add(ImportRow("code", "粘贴 YAML", "粘贴完整配置内容，导入前会校验 YAML 格式。", IconAction("粘贴配置", "PasteConfig", "code", Paste)));
        var resourceHeading = Ui.Label("规则与 Geo 数据", 18, true); resourceHeading.Margin = new Thickness(0, 24, 0, 12);
        resources = new ResourceUpdatePanel(session);
        resourcesTarget = new Border { Child = Ui.Column(resourceHeading, resources), BorderThickness = new Thickness(2), Padding = new Thickness(2), CornerRadius = new CornerRadius(6), Focusable = true }.Id("set-resources");
        resourcesTarget.BorderBrush = Brushes.Transparent; L.Bind(resourcesTarget, AutomationProperties.NameProperty, "规则与 Geo 数据"); overview.Children.Add(resourcesTarget);
        readAgain = IconAction("重新读取", "ConfigReadAgain", "update", Reread);
        closeEditor = IconAction("关闭编辑器", "CloseEditor", "close", CloseEditor);
        saveOnly = IconAction("仅保存", "SaveOnlyConfig", "save", () => Save(false)); save = IconAction("保存并应用", "SaveConfig", "save", () => Save(true), true);
        discard = IconAction("放弃修改", "DiscardConfig", "trash", Reread);
        var leftActions = Ui.Row(readAgain, discard); var rightActions = Ui.Row(closeEditor, saveOnly, save); rightActions.HorizontalAlignment = HorizontalAlignment.Right;
        editorFooter.ColumnDefinitions.Add(new ColumnDefinition()); editorFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editorFooter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); editorFooter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorFooter.Children.Add(leftActions); Grid.SetColumn(rightActions, 1); editorFooter.Children.Add(rightActions);
        editorFooter.SizeChanged += (_, _) => { var compact = editorFooter.ActualWidth < 750; Grid.SetRow(rightActions, compact ? 1 : 0); Grid.SetColumn(rightActions, compact ? 0 : 1); Grid.SetColumnSpan(rightActions, compact ? 2 : 1); };
        keepEditing = IconAction("继续编辑", "ConfigKeepEditing", "edit", () => { SetConfirmation(Confirmation.None); Editor.Focus(); return Task.CompletedTask; });
        confirmDiscard = IconAction("放弃修改", "ConfigConfirmDiscard", "trash", ConfirmDiscard); confirmDiscard.SetResourceReference(Control.ForegroundProperty, "DangerBrush");
        var confirmationLayout = new Grid(); confirmationLayout.ColumnDefinitions.Add(new ColumnDefinition()); confirmationLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        confirmationLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); confirmationLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var confirmationCopy = Ui.Column(confirmationTitle, confirmationText); confirmationCopy.Margin = new Thickness(0, 0, 16, 8); confirmationLayout.Children.Add(confirmationCopy);
        var confirmationActions = Ui.Row(keepEditing, confirmDiscard); Grid.SetColumn(confirmationActions, 1); confirmationLayout.Children.Add(confirmationActions);
        confirmationLayout.SizeChanged += (_, _) => { var compact = confirmationLayout.ActualWidth < 640; Grid.SetRow(confirmationActions, compact ? 1 : 0); Grid.SetColumn(confirmationActions, compact ? 0 : 1); Grid.SetColumnSpan(confirmationActions, compact ? 2 : 1); };
        confirmationPane = Ui.Card(confirmationLayout, 14); confirmationPane.Visibility = Visibility.Collapsed; confirmationPane.Margin = new Thickness(0); confirmationPane.Id("ConfigDiscardConfirmation"); confirmationPane.SetResourceReference(Border.BorderBrushProperty, "WarningBrush");
        BuildEditor(); overviewScroll = Ui.Scroll(overview); var layer = new Grid(); layer.Children.Add(overviewScroll); layer.Children.Add(editPane); SetBody(layer);
        Editor.TextChanged += EditorChanged; code.PositionChanged += UpdatePosition;
        PreviewKeyDown += (_, e) => { if (CanGoBack && e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = RunAsync(() => Save(true)); } };
        code.SaveRequested += () => _ = RunAsync(() => Save(true)); code.CloseRequested += () => _ = RunAsync(CloseEditor);
        code.LimitExceeded += () => Dispatcher.BeginInvoke(new Action(() => { problem = "配置大小超过允许的上限，刚才的输入未被接受。"; UpdateEditorState(); }));
        importUrl.TextChanged += (_, _) => ApplyInteractionState(); importUrl.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && importUrlButton.IsEnabled) { e.Handled = true; await RunAsync(ImportUrl); } };
        AllowDrop = true;
        DragOver += (_, e) => { e.Effects = !Busy && !Session.IsUpdating && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1 && IsYaml(files[0]) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += async (_, e) => { e.Handled = true; if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) await RunAsync(() => ImportFile(files[0])); };
        Session.HostEvent += HostEvent;
        // Cached editors must lock immediately even while another page is installing an update.
        Session.StatusChanged += (_, _) => ApplyInteractionState(); ApplyInteractionState();
    }
    private Button IconAction(string title, string id, string icon, Func<Task> operation, bool primary = false)
    {
        var button = Action(title, id, operation, primary); button.Style = FluentControls.Style("Button"); button.Content = FluentControls.ButtonContent(icon, title); return button;
    }
    private static Border ImportRow(string icon, string title, string description, UIElement? action, UIElement? footer = null)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FluentIcon(icon, 20) { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center });
        var copy = Ui.Column(Ui.Label(title, 14, true), Ui.Hint(description)); copy.Margin = new Thickness(0, 0, 16, 0); Grid.SetColumn(copy, 1); row.Children.Add(copy);
        if (action is not null) { Grid.SetColumn(action, 2); row.Children.Add(action); }
        var content = Ui.Column(row); if (footer is not null) content.Children.Add(footer);
        var card = Ui.Card(content, 16); card.Margin = new Thickness(0, 0, 4, 4); card.CornerRadius = new CornerRadius(4); return card;
    }
    private void BuildEditor()
    {
        for (var index = 0; index < 5; index++) editPane.RowDefinitions.Add(new RowDefinition { Height = index == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var bar = new Grid { Margin = new Thickness(0, 0, 12, 12) }; bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editorPath.TextWrapping = TextWrapping.NoWrap; editorPath.TextTrimming = TextTrimming.CharacterEllipsis; editorPath.Id("ConfigEditorPath");
        var file = new DockPanel(); var icon = new FluentIcon("file", 16) { Margin = new Thickness(0, 0, 8, 0) }; DockPanel.SetDock(icon, Dock.Left); file.Children.Add(icon); file.Children.Add(editorPath); bar.Children.Add(file);
        editorState.Margin = new Thickness(16, 0, 0, 0); Grid.SetColumn(editorState, 1); bar.Children.Add(editorState); editPane.Children.Add(bar);
        editorNotice.Margin = new Thickness(0, 0, 12, 12); Grid.SetRow(editorNotice, 1); editPane.Children.Add(editorNotice);
        code.Margin = new Thickness(0, 0, 12, 0); Grid.SetRow(code, 2); editPane.Children.Add(code);
        var status = new Grid { Margin = new Thickness(0, 8, 12, 12) }; status.ColumnDefinitions.Add(new ColumnDefinition()); status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Children.Add(Ui.Hint("保存时校验 YAML，应用时检查内核配置。")); editorPosition.TextWrapping = TextWrapping.NoWrap; editorPosition.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(editorPosition, 1); status.Children.Add(editorPosition); Grid.SetRow(status, 3); editPane.Children.Add(status);
        var footer = new Grid { Margin = new Thickness(0, 0, 12, 0) }; footer.Children.Add(editorFooter); footer.Children.Add(confirmationPane); Grid.SetRow(footer, 4); editPane.Children.Add(footer);
    }
    private void HostEvent(string name, JsonElement data)
    {
        if (name != "desktop-config-changed" || saving || importing) return;
        if (dirty) { problem = "磁盘上的配置已发生变化。当前草稿已保留，保存时会检查版本冲突。"; UpdateEditorState(); }
        else if (IsActive && !Session.IsUpdating) _ = RunAsync(ReadDocument);
        else loaded = false;
    }
    public override async Task RefreshAsync()
    {
        if (!loaded && !dirty && !Session.IsUpdating) await ReadDocument();
        resources.UpdateAvailability(PageToken); if (IsRunning) await Task.WhenAll(resources.RefreshAsync(PageToken), RefreshSummary());
    }
    protected override void OnStatusChanged()
    {
        ApplyInteractionState(); UpdateSummary(); var phase = Session.Status.Text("phase");
        if (phase != lastPhase) { lastPhase = phase; if (phase == "running" && IsActive) _ = RunAsync(RefreshAsync); }
    }
    protected override void OnLanguageChanged() { ApplyInteractionState(); UpdateSummary(); UpdatePosition(); }
    protected override Task LiveAsync(CancellationToken cancellationToken) => PollAsync(TimeSpan.FromMilliseconds(1500), async () =>
    {
        if (resources.NeedsRefresh) await resources.RefreshAsync(cancellationToken);
        if (Environment.TickCount64 - summaryReadAt >= 30000) await RefreshSummary();
    }, cancellationToken);
    private async Task RefreshSummary()
    {
        if (!IsRunning) return; var identity = ControllerIdentity;
        try
        {
            var data = await Task.WhenAll(Get("proxies"), Get("rules")); if (!IsRunning || identity != ControllerIdentity) return;
            groupCount = data[0].Get("proxies").Fields().Count(item => item.Name != "GLOBAL" && ProxyNodeInfo.From(item.Name, item.Value).IsGroup);
            ruleCount = data[1].Get("rules").Items().Count(); summaryReadAt = Environment.TickCount64; UpdateSummary();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { summaryReadAt = Environment.TickCount64; /* File editing remains available when metadata cannot be read. */ }
    }
    private void UpdateSummary()
    {
        if (documentPath.Length == 0) documentPath = Session.Status.Text("configPath");
        path.Text = documentPath; path.ToolTip = documentPath; fileName.Text = documentPath.Length > 0 ? Path.GetFileName(documentPath) : L.T("正在读取配置…");
        counts.Text = IsRunning ? groupCount.HasValue && ruleCount.HasValue ? L.F("{0} 个策略组 · {1} 条规则", groupCount, ruleCount) : L.T("正在读取配置统计…") : InactiveMessage;
    }
    private Task ReadDocument() => ReadDocument(false);
    private Task ReadDocument(bool discardDraft)
    {
        if (readTask is { IsCompleted: false }) return readTask; readTask = ReadDocumentCore(discardDraft); return readTask;
    }
    private async Task ReadDocumentCore(bool discardDraft)
    {
        EnsureNoUpdate(); var generation = ++documentGeneration; var draft = draftGeneration; reading = true; problem = ""; ApplyInteractionState();
        try
        {
            var document = ValidateDocument(await Command("desktop_read_config"));
            if (generation != documentGeneration || draft != draftGeneration || (dirty && !discardDraft)) return;
            ApplyDocument(document, true); saved = reloadPending = false; await Session.SetDirtyAsync(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw EditorFailure(error); }
        finally { reading = false; ApplyInteractionState(); }
    }
    private static JsonElement ValidateDocument(JsonElement document)
    {
        var filePath = document.Get("path"); var content = document.Get("content"); var hash = document.Get("revision"); var size = document.Get("max_size"); var write = document.Get("writable");
        if (filePath.ValueKind != JsonValueKind.String || filePath.GetString()!.Length is 0 or > 4096 || content.ValueKind != JsonValueKind.String || hash.ValueKind != JsonValueKind.String || hash.GetString()!.Length is 0 or > 512 || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var limit) || limit < 1 || write.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False) || Encoding.UTF8.GetByteCount(content.GetString()!) > Math.Min(limit, MaximumConfigBytes))
            throw new DesktopException("config_response_invalid", "返回的配置文件信息不完整，请重新读取。");
        return document;
    }
    private void ApplyDocument(JsonElement document, bool resetPosition)
    {
        ++documentGeneration; loaded = true; applyingDocument = true;
        try
        {
            originalContent = document.Text("content"); revision = document.Text("revision"); documentPath = document.Text("path");
            maxBytes = (int)Math.Min(MaximumConfigBytes, document.Number("max_size", MaximumConfigBytes)); code.MaximumBytes = maxBytes;
            writable = document.Get("writable").ValueKind != JsonValueKind.False; code.SetDocument(originalContent, resetPosition);
            dirty = conflict = false; problem = ""; Session.IsDirty = false;
        }
        finally { applyingDocument = false; }
        editorBytes = Encoding.UTF8.GetByteCount(originalContent); UpdateSummary(); ApplyInteractionState(); UpdatePosition();
    }
    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (applyingDocument) return; ++draftGeneration;
        dirty = ConfigCodeEditor.DisplayText(Editor.Text) != ConfigCodeEditor.DisplayText(originalContent); Session.IsDirty = dirty; saved = false;
        editorBytes = Encoding.UTF8.GetByteCount(ConfigCodeEditor.SaveText(Editor.Text, originalContent));
        if (!conflict) problem = ""; ApplyInteractionState(); UpdatePosition();
    }
    private void UpdatePosition() => editorPosition.Text = L.F("第 {0} 行，第 {1} 列", code.Line, code.Column) + "   YAML · UTF-8   " + Ui.Bytes(editorBytes);
    private async Task OpenEditor()
    {
        EnsureNoUpdate();
        if (saving || importing) throw new InvalidOperationException(L.T("配置操作正在进行，请稍后重试。"));
        SetEditorVisible(true); if (reading && readTask is not null) await readTask; else if (!loaded) await ReadDocument();
        if (!Session.IsUpdating) Editor.Focus();
    }
    private void SetEditorVisible(bool visible)
    {
        editPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; overviewScroll.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        Tools.Visibility = visible ? Visibility.Collapsed : Visibility.Visible; SetConfirmation(Confirmation.None); BackStateChanged?.Invoke(); if (visible) FluentMotion.Enter(editPane, true);
    }
    public async Task<bool> GoBackAsync() { await CloseEditor(); return !CanGoBack; }
    private Task CloseEditor()
    {
        if (Busy) { Message("配置操作正在进行，请完成后再关闭编辑器。"); return Task.CompletedTask; }
        if (confirmation != Confirmation.None) { SetConfirmation(Confirmation.None); Editor.Focus(); return Task.CompletedTask; }
        if (dirty) { SetConfirmation(Confirmation.Close); return Task.CompletedTask; }
        SetEditorVisible(false); return Task.CompletedTask;
    }
    private Task Reread()
    {
        EnsureAvailable(); if (dirty) { SetConfirmation(Confirmation.Read); return Task.CompletedTask; } return ReadDocument(true);
    }
    private void SetConfirmation(Confirmation value)
    {
        confirmation = value; editorFooter.Visibility = value == Confirmation.None ? Visibility.Visible : Visibility.Collapsed; confirmationPane.Visibility = value == Confirmation.None ? Visibility.Collapsed : Visibility.Visible;
        if (value != Confirmation.None)
        {
            confirmationTitle.Text = L.T(value == Confirmation.Close ? "放弃修改并关闭？" : "放弃修改并重新读取？"); confirmationText.Text = L.T("当前未保存的内容将丢失，磁盘上的配置不会被修改。");
            confirmDiscard.Content = FluentControls.ButtonContent(value == Confirmation.Close ? "trash" : "update", value == Confirmation.Close ? "放弃并关闭" : "放弃并重新读取");
            L.Bind(confirmDiscard, AutomationProperties.NameProperty, value == Confirmation.Close ? "放弃并关闭" : "放弃并重新读取");
            FluentMotion.Enter(confirmationPane); Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => keepEditing.Focus()));
        }
        ApplyInteractionState();
    }
    private async Task ConfirmDiscard()
    {
        EnsureAvailable(); var action = confirmation;
        if (action == Confirmation.Close) { await ResetDraft(); SetEditorVisible(false); }
        else if (action == Confirmation.Read) { SetConfirmation(Confirmation.None); await ReadDocument(true); Editor.Focus(); }
    }
    private async Task ResetDraft()
    {
        ++documentGeneration; ++draftGeneration; applyingDocument = true;
        try { code.SetDocument(originalContent, true); editorBytes = Encoding.UTF8.GetByteCount(originalContent); dirty = conflict = saved = reloadPending = false; problem = ""; loaded = false; }
        finally { applyingDocument = false; }
        await Session.SetDirtyAsync(false); ApplyInteractionState();
    }
    private async Task Save(bool apply)
    {
        EnsureAvailable(); if (!loaded || !writable || conflict || confirmation != Confirmation.None || (!dirty && !reloadPending)) return;
        var content = ConfigCodeEditor.SaveText(Editor.Text, originalContent);
        if (Encoding.UTF8.GetByteCount(content) > maxBytes) { problem = "配置大小超过允许的上限，请缩减内容后再保存。"; UpdateEditorState(); return; }
        saving = true; saved = false; problem = ""; ApplyInteractionState(); var fileSaved = false;
        try
        {
            // A reload retry must also check the saved revision before applying an external file.
            var document = ValidateDocument(await Command("desktop_save_config", new { content, expectedRevision = revision }));
            ApplyDocument(document, false); fileSaved = true; reloadPending = true; await Session.SetDirtyAsync(false);
            if (apply) { EnsureNoUpdate(); reloading = true; UpdateEditorState(); await Command("desktop_reload_config", new { force = true }); reloadPending = false; _ = RunAsync(RefreshSummary); }
            saved = true; Message(apply ? IsRunning ? "配置已保存并应用。" : "配置已保存并校验，内核启动时将使用此配置。" : "配置已保存，可以稍后应用。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw EditorFailure(error, fileSaved); }
        finally { saving = reloading = false; ApplyInteractionState(); }
    }
    private Exception EditorFailure(Exception error, bool fileSaved = false)
    {
        var code = error is DesktopException desktop ? desktop.Code : "network";
        if (code == "config_conflict") conflict = true; if (code is "config_read_only" or "config_missing") writable = false;
        problem = fileSaved ? code is "config_invalid" or "reload_invalid" ? "配置已保存，但内核校验未通过。请修正配置后重新应用。" : "配置已保存，但应用失败。可以重试应用，或重新读取磁盘配置。" : code switch
        {
            "config_conflict" => "磁盘配置已被修改，当前草稿已保留。请重新读取后再保存。",
            "config_read_only" => "配置文件为只读，请先调整文件权限，再重新读取。",
            "config_missing" => "无法读取配置文件，请检查文件是否存在及访问权限。",
            "config_too_large" or "request_too_large" => "配置大小超过允许的上限，请缩减内容后再保存。",
            "config_invalid" => "YAML 配置格式无效。请检查内容后再保存，磁盘文件未被修改。",
            "reload_invalid" => "内核未接受此配置，请修正配置后重试。",
            "desktop_busy" or "config_busy" => "配置操作正在进行，请稍后重试。",
            "config_path" => "配置文件路径不可用，请检查本地配置目录。",
            "config_response_invalid" => "返回的配置文件信息不完整，请重新读取。",
            "update_in_progress" => "正在安装应用更新，请等待更新完成后再编辑、保存或导入配置。",
            "invalid_url" => "请输入有效的 HTTP 或 HTTPS 订阅地址。",
            _ => "无法完成配置操作，请检查本地服务或网络后重试。"
        };
        UpdateEditorState(); return new InvalidOperationException(L.T(problem));
    }
    private void ApplyInteractionState()
    {
        var updating = Session.IsUpdating; var available = !Busy && !updating;
        overview.IsEnabled = available; AllowDrop = available; importButton.IsEnabled = available; openEditor.IsEnabled = available; reload.IsEnabled = available;
        importUrlButton.IsEnabled = available && !string.IsNullOrWhiteSpace(importUrl.Text);
        Editor.IsReadOnly = !loaded || !writable || Busy || updating || confirmation != Confirmation.None;
        readAgain.IsEnabled = available; discard.IsEnabled = available && dirty; closeEditor.IsEnabled = !Busy; keepEditing.IsEnabled = !Busy; confirmDiscard.IsEnabled = available;
        save.IsEnabled = available && loaded && writable && !conflict && confirmation == Confirmation.None && (dirty || reloadPending);
        saveOnly.IsEnabled = available && loaded && writable && !conflict && confirmation == Confirmation.None && dirty;
        resources.UpdateAvailability(PageToken); UpdateEditorState();
        if (updating && !updateWasActive) Message("正在安装应用更新，配置编辑与导入已暂时锁定。请等待更新完成。");
        else if (!updating && updateWasActive) Message("应用更新已结束，可以继续编辑配置。"); updateWasActive = updating;
    }
    private void UpdateEditorState()
    {
        editorPath.Text = documentPath.Length > 0 ? documentPath : L.T("尚未读取配置文件"); editorPath.ToolTip = documentPath;
        editorState.Text = L.T(reading ? "正在读取…" : saving ? reloading ? "正在应用…" : "正在保存…" : !writable ? "只读" : dirty ? "未保存" : reloadPending ? "等待应用" : loaded ? "YAML" : "不可用");
        editorState.SetResourceReference(TextBlock.ForegroundProperty, loaded && !conflict && writable ? "AccentBrush" : "TextSecondaryBrush");
        editorNotice.Text = L.T(problem.Length > 0 ? problem : !writable ? "当前配置仅供查看，重新读取可以重新检查写入权限。" : saved ? reloadPending ? "配置已保存，点击“保存并应用”使其生效。" : IsRunning ? "配置已保存并应用。" : "配置已保存并校验，内核启动时将使用此配置。" : "直接编辑当前 YAML 文件。Tab 缩进两个空格，Ctrl+S 保存并应用，Ctrl+F 查找。");
        editorNotice.SetResourceReference(TextBlock.ForegroundProperty, problem.Length > 0 ? "DangerBrush" : saved ? "SuccessBrush" : "TextSecondaryBrush");
        var title = saving ? reloading ? "正在应用…" : "正在保存…" : reloadPending && !dirty ? "重试应用" : "保存并应用";
        save.Content = FluentControls.ButtonContent(saving ? "update" : "save", title); L.Bind(save, AutomationProperties.NameProperty, title);
        readAgain.Content = FluentControls.ButtonContent("update", loaded ? "重新读取" : "重试读取"); L.Bind(readAgain, AutomationProperties.NameProperty, loaded ? "重新读取" : "重试读取");
    }
    private void EnsureNoUpdate() { if (Session.IsUpdating) throw new InvalidOperationException(L.T("正在安装应用更新，请等待更新完成后再编辑、保存或导入配置。")); }
    private void EnsureAvailable() { EnsureNoUpdate(); if (Busy) throw new InvalidOperationException(L.T("配置操作正在进行，请稍后重试。")); }
    private async Task Reload()
    {
        EnsureAvailable(); if (!await CanLeaveAsync()) return; EnsureAvailable(); saving = reloading = true; ApplyInteractionState();
        try
        {
            await Command("desktop_reload_config", new { force = true }); reloadPending = false;
            Message(IsRunning ? "配置已重新加载。" : "配置已校验，内核启动时将使用此配置。");
            try { await ReadDocument(); } catch (Exception) { Message("配置已重新加载，但暂时无法重新读取文件。请稍后重试读取。"); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw EditorFailure(error); }
        finally { saving = reloading = false; ApplyInteractionState(); }
    }
    public override async Task<bool> CanLeaveAsync()
    {
        if (Busy) { Message("配置操作正在进行，请完成后再离开此页。"); return false; }
        if (!dirty) { await Session.SetDirtyAsync(false); return true; }
        if (Session.IsUpdating) { Message("正在安装应用更新，请等待更新完成后再处理配置草稿。"); return false; }
        SetConfirmation(Confirmation.None);
        var choice = MessageBox.Show(Window.GetWindow(this), L.T("配置有未保存的修改，是否保存后继续？选择“否”将放弃这些修改。"), L.T("保存配置修改"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.Cancel) return false;
        try { if (choice == MessageBoxResult.Yes) { await Save(false); if (dirty) return false; } else await ResetDraft(); return true; }
        catch (Exception error) { Message(error.Message, true); return false; }
    }
    private async Task ChooseFile()
    {
        EnsureAvailable(); var dialog = new OpenFileDialog { Filter = L.T("YAML 配置|*.yaml;*.yml|所有文件|*.*"), CheckFileExists = true, Multiselect = false, Title = L.T("导入配置") };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await ImportFile(dialog.FileName);
    }
    private static bool IsYaml(string file) => Path.GetExtension(file).Equals(".yaml", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".yml", StringComparison.OrdinalIgnoreCase);
    private async Task ImportFile(string file)
    {
        EnsureAvailable(); if (!IsYaml(file)) throw new InvalidOperationException(L.T("请导入 .yaml 或 .yml 配置文件。")); importing = true; ApplyInteractionState(); string content;
        try
        {
            // Bound the read itself: a file may grow after checking its initial length.
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            if (stream.Length > maxBytes) throw new DesktopException("config_too_large", ""); var bytes = new byte[maxBytes + 1]; var length = 0;
            while (length < bytes.Length) { var received = await stream.ReadAsync(bytes.AsMemory(length)); if (received == 0) break; length += received; }
            if (length > maxBytes) throw new DesktopException("config_too_large", "");
            var offset = length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0; content = new UTF8Encoding(false, true).GetString(bytes, offset, length - offset);
        }
        catch (DesktopException error) { throw EditorFailure(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException) { throw new InvalidOperationException(L.T("无法读取配置文件，请确认文件可访问且使用 UTF-8 编码。")); }
        finally { importing = false; ApplyInteractionState(); }
        await Import(content, false);
    }
    private async Task ImportUrl()
    {
        var url = importUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || string.IsNullOrEmpty(parsed.Host) || parsed.UserInfo.Length > 0) throw new InvalidOperationException(L.T("请输入有效的 HTTP 或 HTTPS 订阅地址。"));
        if (await Import(url, true)) importUrl.Clear();
    }
    private async Task Paste()
    {
        EnsureAvailable(); var dialog = new Window { Owner = Window.GetWindow(this), Title = L.T("粘贴配置"), Width = 650, Height = 460, MinWidth = 440, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(Control.BackgroundProperty, "AppBackgroundBrush"); dialog.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        var input = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12), MaxLength = maxBytes, FontFamily = new FontFamily("Cascadia Mono, Consolas") }.Id("ConfigPasteInput");
        var accept = L.Bind(new Button { Padding = new Thickness(18, 6, 18, 6), IsEnabled = false }.Id("ConfigPasteAccept"), ContentControl.ContentProperty, "导入配置"); FluentControls.Primary(accept);
        var cancel = L.Bind(new Button { Padding = new Thickness(18, 6, 18, 6), IsCancel = true }.Id("ConfigPasteCancel"), ContentControl.ContentProperty, "取消");
        var layout = new DockPanel { Margin = new Thickness(20) }; var hint = Ui.Hint("粘贴完整 YAML 配置，导入后可在当前配置中编辑。", 14); hint.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);
        var actions = Ui.Row(cancel, accept); actions.HorizontalAlignment = HorizontalAlignment.Right; actions.Margin = new Thickness(0, 16, 0, 0); DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions); layout.Children.Add(input); dialog.Content = layout;
        input.TextChanged += (_, _) => accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text) && Encoding.UTF8.GetByteCount(input.Text) <= maxBytes;
        accept.Click += (_, _) => dialog.DialogResult = true; dialog.Loaded += (_, _) => input.Focus(); if (dialog.ShowDialog() == true) await Import(input.Text, false);
    }
    private async Task<bool> Import(string value, bool url)
    {
        EnsureAvailable(); if (!url && Encoding.UTF8.GetByteCount(value) > maxBytes) throw new InvalidOperationException(L.T("配置大小超过允许的上限，请缩减内容后再保存。"));
        if (!await CanLeaveAsync()) return false; EnsureAvailable();
        if (MessageBox.Show(Window.GetWindow(this), L.T("导入将替换当前配置，原配置会由后台备份。继续导入？"), L.T("导入配置"), MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return false;
        if (!loaded) await ReadDocument(); EnsureAvailable(); importing = true; ApplyInteractionState();
        try
        {
            var document = url ? await Command("desktop_import_config_url", new { url = value, expectedRevision = revision }) : await Command("desktop_import_config", new { content = value, expectedRevision = revision });
            ApplyDocument(ValidateDocument(document), true); reloadPending = true; saved = true; await Session.SetDirtyAsync(false); Message("配置已导入。点击“重新加载”应用到当前内核。"); return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw EditorFailure(error); }
        finally { importing = false; ApplyInteractionState(); }
    }
    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (target.Action != "edit" && reading && readTask is not null) await readTask;
        if (target.Action == "edit") await OpenEditor();
        else if (target.Action == "import-file" && target.Target is not null) await ImportFile(target.Target);
        else if (target.Action == "import") await ChooseFile();
        else if (target.Target == "set-resources")
        {
            if (CanGoBack && !await CanLeaveAsync()) return; SetEditorVisible(false);
            await Dispatcher.InvokeAsync(() => { resourcesTarget.BringIntoView(); resourcesTarget.Focus(); resourcesTarget.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); FluentMotion.Flash(resourcesTarget); }, DispatcherPriority.Loaded);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += (_, _) => { timer.Stop(); resourcesTarget.BorderBrush = Brushes.Transparent; }; timer.Start();
        }
    }
}
