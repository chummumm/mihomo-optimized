using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.IO.Path;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ProfilesPage : NativePage, IPageBackNavigation
{
    private const int MaximumConfigBytes = 4 * 1024 * 1024;
    private readonly TextBlock fileName = Ui.Text("—", 18, true);
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
    private readonly Grid editorContent = new();
    private readonly Border editorHeader = new();
    private readonly Border editorNoticeFrame = new();
    private readonly Ellipse editorDot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button editorCrumb = new() { Style = FluentControls.Style("Ghost"), Padding = new Thickness(2, 0, 2, 0), BorderThickness = new Thickness(0) };
    private readonly FluentIcon editorSeparator = new("chevron-right", 12);
    private readonly TextBlock editorTitle = Ui.Label("编辑配置", 28, true);
    private long editorTransition;
    private bool editorOpen;
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
    private readonly MenuItem saveOnly;
    private readonly MenuItem discard;
    private readonly Button closeEditor;
    private readonly ResourceUpdatePanel resources;
    private readonly Border resourcesTarget;
    private string revision = "", originalContent = "", documentPath = "", problem = "", lastPhase = "";
    private bool applyingDocument, loaded, reading, saving, reloading, importing, dirty, conflict, saved, reloadPending, updateWasActive;
    private bool writable = true;
    private bool importOpening, leaveOpening;
    private long documentGeneration, draftGeneration, summaryReadAt;
    private int? groupCount, ruleCount;
    private int maxBytes = MaximumConfigBytes;
    private int editorBytes;
    private Task? readTask;
    private Confirmation confirmation;
    private enum Confirmation { None, Close, Read }
    private bool Busy => reading || saving || importing;
    public bool CanGoBack => editorOpen;
    public event Action? BackStateChanged;

    public ProfilesPage(AppSession session) : base(session, "profiles", "配置", "管理当前 YAML，导入订阅并更新规则与 Geo 数据")
    {
        importButton = IconAction("导入配置", "ImportConfig", "arrow-import", ChooseFile, true); Tools.Children.Add(importButton);
        SetHeaderMeta(null);
        openEditor = IconAction("编辑配置文件", "EditConfig", "edit", OpenEditor);
        reload = IconAction("重新加载已保存配置", "ReloadConfig", "update", Reload);
        var more = FluentControls.IconButton("more", "更多配置操作", "ConfigMore"); var menu = new ContextMenu();
        AddDirectory("打开配置文件夹", "OpenConfigFolder", "config"); AddDirectory("打开日志文件夹", "OpenLogsFolder", "logs");
        more.ContextMenu = menu; more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
        void AddDirectory(string title, string id, string kind)
        {
            var item = L.Bind(new MenuItem().Id(id), HeaderedItemsControl.HeaderProperty, title);
            item.Click += async (_, _) => await RunAsync(() => Command("desktop_open_directory", new { kind })); menu.Items.Add(item);
        }
        var current = new Grid(); current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); current.ColumnDefinitions.Add(new ColumnDefinition()); current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var fileIcon = new FluentIcon("document-text", 24); fileIcon.SetResourceReference(Control.ForegroundProperty, "AccentLinkBrush");
        var iconWell = new Border { Child = fileIcon, Width = 48, Height = 48, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center }; iconWell.SetResourceReference(Border.BackgroundProperty, "ControlAltBrush"); current.Children.Add(iconWell);
        path.TextWrapping = TextWrapping.NoWrap; path.TextTrimming = TextTrimming.CharacterEllipsis; path.FontFamily = new FontFamily("Cascadia Mono, Consolas"); path.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); path.LineHeight = 16; path.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; path.Id("CurrentConfigPath");
        fileName.TextWrapping = TextWrapping.NoWrap; fileName.TextTrimming = TextTrimming.CharacterEllipsis; fileName.LineHeight = 24; fileName.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; fileName.Id("CurrentConfigName"); fileName.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        var inUse = Ui.Hint("正在使用"); inUse.LineHeight = 16; inUse.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var identity = Ui.Column(inUse, fileName, path); identity.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(identity, 1); current.Children.Add(identity);
        var countsBadge = new Border { Child = counts, Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(4), Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; countsBadge.SetResourceReference(Border.BackgroundProperty, "ControlAltBrush"); Grid.SetColumn(countsBadge, 2); current.Children.Add(countsBadge);
        var fileActions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) }; foreach (var button in new[] { openEditor, reload, more }) { button.Margin = new Thickness(0, 0, 8, 0); fileActions.Children.Add(button); }
        var currentCard = Ui.Card(Ui.Column(current, fileActions), 0); currentCard.Padding = new Thickness(24, 20, 24, 20); currentCard.Margin = new Thickness(0); currentCard.CornerRadius = new CornerRadius(4); overview.Children.Add(currentCard);
        var importHeading = new DockPanel { Margin = new Thickness(1, 28, 0, 8) }; var aside = Ui.Hint("替换当前主配置，原文件自动备份"); aside.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); DockPanel.SetDock(aside, Dock.Right); importHeading.Children.Add(aside); importHeading.Children.Add(Ui.Label("导入新配置", 14, true)); overview.Children.Add(importHeading);
        overview.Children.Add(ImportRow("file", "从文件导入", "YAML / YML，最大 4 MiB。也可以把文件直接拖进窗口。", Action("选择文件", "ImportConfigFile", ChooseFile)));
        importUrl.MinWidth = 0; importUrl.HorizontalAlignment = HorizontalAlignment.Stretch; importUrl.Margin = new Thickness(0, 0, 8, 0); importUrl.ToolTip = "https://";
        importUrlButton = Action("导入", "ImportConfigUrl", ImportUrl); importUrlButton.Margin = new Thickness(0);
        var urlRow = new Grid(); urlRow.ColumnDefinitions.Add(new ColumnDefinition()); urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); urlRow.Children.Add(importUrl); Grid.SetColumn(importUrlButton, 1); urlRow.Children.Add(importUrlButton);
        overview.Children.Add(ImportRow("link", "从 URL 导入", "返回完整 YAML 配置的 HTTP / HTTPS 地址", null, urlRow));
        overview.Children.Add(ImportRow("code", "粘贴 YAML", "把整份配置粘贴到文本框中", Action("粘贴 YAML", "PasteConfig", Paste)));
        var resourceHeading = Ui.Label("规则与 Geo 数据", 14, true); resourceHeading.Margin = new Thickness(1, 0, 0, 8); resources = new ResourceUpdatePanel(session);
        resourcesTarget = new Border { Child = Ui.Column(resourceHeading, resources), BorderThickness = new Thickness(2), Padding = new Thickness(0), Margin = new Thickness(-2, 26, -2, -2), CornerRadius = new CornerRadius(4), Focusable = true }.Id("set-resources");
        resourcesTarget.BorderBrush = Brushes.Transparent; L.Bind(resourcesTarget, AutomationProperties.NameProperty, "规则与 Geo 数据"); overview.Children.Add(resourcesTarget);
        SizeChanged += (_, _) => { currentCard.Padding = PageContainerWidth <= 560 ? new Thickness(16) : new Thickness(24, 20, 24, 20); countsBadge.Visibility = PageContainerWidth <= 560 ? Visibility.Collapsed : Visibility.Visible; UpdateEditorMetrics(); };
        readAgain = IconAction("从文件重新读取", "ConfigReadAgain", "update", Reread);
        closeEditor = IconAction("关闭", "CloseEditor", "close", CloseEditor);
        save = IconAction("保存并重新加载", "SaveConfig", "save", () => Save(true), true);
        saveOnly = L.Bind(new MenuItem().Id("SaveOnlyConfig"), HeaderedItemsControl.HeaderProperty, "仅保存"); saveOnly.Click += async (_, _) => await RunAsync(() => Save(false));
        discard = L.Bind(new MenuItem().Id("DiscardConfig"), HeaderedItemsControl.HeaderProperty, "放弃修改"); discard.Click += async (_, _) => await RunAsync(Reread);
        var editorCommands = new ContextMenu(); editorCommands.Items.Add(saveOnly); editorCommands.Items.Add(discard); save.ContextMenu = editorCommands; editorFooter.ContextMenu = editorCommands;
        var rightActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; closeEditor.Margin = new Thickness(0, 0, 8, 0); save.Margin = new Thickness(0); rightActions.Children.Add(closeEditor); rightActions.Children.Add(save);
        editorFooter.Margin = new Thickness(0, 4, 0, 0); editorFooter.ColumnDefinitions.Add(new ColumnDefinition()); editorFooter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editorFooter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); editorFooter.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); editorFooter.Children.Add(readAgain); readAgain.HorizontalAlignment = HorizontalAlignment.Left; Grid.SetColumn(rightActions, 1); editorFooter.Children.Add(rightActions);
        editorFooter.SizeChanged += (_, _) => { var compact = editorFooter.ActualWidth < readAgain.DesiredSize.Width + rightActions.DesiredSize.Width + 8; Grid.SetRow(rightActions, compact ? 1 : 0); Grid.SetColumn(rightActions, compact ? 0 : 1); Grid.SetColumnSpan(rightActions, compact ? 2 : 1); rightActions.Margin = new Thickness(0, compact ? 8 : 0, 0, 0); };
        keepEditing = IconAction("继续编辑", "ConfigKeepEditing", "edit", () => { SetConfirmation(Confirmation.None); Editor.Focus(); return Task.CompletedTask; });
        confirmDiscard = IconAction("放弃修改", "ConfigConfirmDiscard", "trash", ConfirmDiscard); confirmDiscard.SetResourceReference(Control.ForegroundProperty, "DangerBrush");
        var confirmationLayout = new Grid(); confirmationLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) }); confirmationLayout.ColumnDefinitions.Add(new ColumnDefinition()); confirmationLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); confirmationLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); confirmationLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var warning = new FluentIcon("warning", 20) { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left }; warning.SetResourceReference(Control.ForegroundProperty, "WarningBrush"); confirmationLayout.Children.Add(warning);
        confirmationTitle.LineHeight = confirmationText.LineHeight = 20; confirmationTitle.LineStackingStrategy = confirmationText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; confirmationText.FontSize = 14; confirmationText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); confirmationText.Margin = new Thickness(0, 2, 0, 0);
        var confirmationCopy = Ui.Column(confirmationTitle, confirmationText); confirmationCopy.Margin = new Thickness(0, 0, 14, 0); Grid.SetColumn(confirmationCopy, 1); confirmationLayout.Children.Add(confirmationCopy);
        var confirmationActions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }; keepEditing.Margin = new Thickness(0, 0, 8, 0); confirmDiscard.Margin = new Thickness(0); confirmationActions.Children.Add(keepEditing); confirmationActions.Children.Add(confirmDiscard); Grid.SetColumn(confirmationActions, 2); confirmationLayout.Children.Add(confirmationActions);
        confirmationLayout.SizeChanged += (_, _) => { var compact = confirmationLayout.ActualWidth < 34 + 260 + confirmationActions.DesiredSize.Width; Grid.SetRow(confirmationActions, compact ? 1 : 0); Grid.SetColumn(confirmationActions, compact ? 0 : 2); Grid.SetColumnSpan(confirmationActions, compact ? 3 : 1); confirmationActions.Margin = new Thickness(0, compact ? 12 : 0, 0, 0); };
        confirmationPane = Ui.Card(confirmationLayout, 0); confirmationPane.Padding = new Thickness(14, 12, 12, 12); confirmationPane.CornerRadius = new CornerRadius(8); confirmationPane.Visibility = Visibility.Collapsed; confirmationPane.Margin = new Thickness(0); confirmationPane.Id("ConfigDiscardConfirmation"); confirmationPane.SetResourceReference(Border.BackgroundProperty, "WarningBackgroundBrush"); confirmationPane.RenderTransformOrigin = new Point(.5, 1);
        BuildEditor(); overviewScroll = new ScrollViewer(); SetScrollableBody(overview, overviewScroll);
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
        var button = Action(title, id, operation, primary); button.Content = FluentControls.ButtonContent(icon, title); return button;
    }
    private Border ImportRow(string icon, string title, string description, UIElement? action, UIElement? footer = null)
    {
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.Children.Add(new FluentIcon(icon, 20) { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center });
        var label = Ui.Label(title); label.LineHeight = 20; label.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; var hint = Ui.Hint(description); hint.LineHeight = 16; hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var copy = Ui.Column(label, hint); copy.Margin = new Thickness(0, 0, 16, 0); copy.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(copy, 1); row.Children.Add(copy);
        if (action is not null) { Grid.SetColumn(action, 2); row.Children.Add(action); if (action is FrameworkElement element) { element.Margin = new Thickness(0); element.VerticalAlignment = VerticalAlignment.Center; } }
        var rowFrame = new Border { Child = row, Padding = new Thickness(16, 12, 16, 12), MinHeight = 68 }; var content = Ui.Column(rowFrame);
        Border? foot = null;
        if (footer is not null) { foot = new Border { Child = footer, Padding = new Thickness(52, 12, 16, 16), BorderThickness = new Thickness(0, 1, 0, 0) }; foot.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); foot.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush"); content.Children.Add(foot); }
        var card = Ui.Card(content, 0); card.Margin = new Thickness(0, 0, 0, 4); card.CornerRadius = new CornerRadius(4);
        card.SizeChanged += (_, _) => { var compact = PageContainerWidth <= 560; if (action is not null) { Grid.SetRow(action, compact ? 1 : 0); Grid.SetColumn(action, compact ? 1 : 2); if (action is FrameworkElement element) { element.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right; element.Margin = new Thickness(0, compact ? 4 : 0, 0, 0); } } if (foot is not null) foot.Padding = new Thickness(compact ? 16 : 52, 12, 16, 16); }; return card;
    }
    private void BuildEditor()
    {
        editPane.SetResourceReference(Panel.BackgroundProperty, "SolidBaseBrush"); editPane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); editPane.RowDefinitions.Add(new RowDefinition());
        var layer = new Border(); layer.SetResourceReference(Border.BackgroundProperty, "LayerBrush"); Grid.SetRowSpan(layer, 2); editPane.Children.Add(layer);
        var crumbs = new Grid(); crumbs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); crumbs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); crumbs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); crumbs.ColumnDefinitions.Add(new ColumnDefinition());
        var back = FluentControls.IconButton("arrow-left", "返回", "ConfigEditorBack"); back.Click += async (_, _) => await RunAsync(CloseEditor); crumbs.Children.Add(back);
        var parent = Ui.Label("配置", 28, true); parent.LineHeight = 36; parent.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); parent.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); editorCrumb.Content = parent; editorCrumb.Click += async (_, _) => await RunAsync(CloseEditor); Grid.SetColumn(editorCrumb, 1); crumbs.Children.Add(editorCrumb);
        editorSeparator.Margin = new Thickness(8, 0, 8, 0); editorSeparator.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush"); Grid.SetColumn(editorSeparator, 2); crumbs.Children.Add(editorSeparator);
        editorTitle.LineHeight = 36; editorTitle.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; editorTitle.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); editorTitle.TextWrapping = TextWrapping.NoWrap; editorTitle.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetColumn(editorTitle, 3); crumbs.Children.Add(editorTitle); editorHeader.Child = crumbs; editPane.Children.Add(editorHeader);
        for (var index = 0; index < 5; index++) editorContent.RowDefinitions.Add(new RowDefinition { Height = index == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto, MinHeight = index == 2 ? 120 : 0 }); Grid.SetRow(editorContent, 1); editPane.Children.Add(editorContent);
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 8) }; bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        editorPath.FontFamily = new FontFamily("Cascadia Mono, Consolas"); editorPath.TextWrapping = TextWrapping.NoWrap; editorPath.TextTrimming = TextTrimming.CharacterEllipsis; editorPath.Id("ConfigEditorPath");
        var file = new DockPanel { Height = 28 }; var icon = new FluentIcon("file", 14) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; icon.SetResourceReference(Control.ForegroundProperty, "AccentLinkBrush"); DockPanel.SetDock(icon, Dock.Left); file.Children.Add(icon); file.Children.Add(editorPath); bar.Children.Add(file);
        var state = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; state.Children.Add(editorDot); state.Children.Add(editorState); Grid.SetColumn(state, 1); bar.Children.Add(state); editorContent.Children.Add(bar);
        editorNotice.FontSize = 12; editorNotice.LineHeight = 16; editorNotice.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; editorNoticeFrame.Child = editorNotice; editorNoticeFrame.Padding = new Thickness(12, 8, 12, 8); editorNoticeFrame.BorderThickness = new Thickness(1); editorNoticeFrame.CornerRadius = new CornerRadius(4); editorNoticeFrame.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(editorNoticeFrame, 1); editorContent.Children.Add(editorNoticeFrame);
        code.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(code, 2); editorContent.Children.Add(code);
        var status = new Grid { Margin = new Thickness(0, 0, 0, 8) }; status.ColumnDefinitions.Add(new ColumnDefinition()); status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var note = Ui.Hint("保存会校验 YAML 基本结构；完整配置校验在重新加载时进行。草稿仅保留在内存中，关闭编辑器前会确认未保存的修改。"); note.MaxWidth = 65 * 7.2; note.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); note.Margin = new Thickness(0, 0, 16, 0); status.Children.Add(note);
        editorPosition.TextWrapping = TextWrapping.NoWrap; editorPosition.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); editorPosition.Margin = new Thickness(0, 0, 16, 0); Grid.SetColumn(editorPosition, 1); status.Children.Add(editorPosition); var encoding = Ui.Muted("YAML · UTF-8", 12); encoding.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); Grid.SetColumn(encoding, 2); status.Children.Add(encoding); Grid.SetRow(status, 3); editorContent.Children.Add(status);
        var footer = new Grid(); footer.Children.Add(editorFooter); footer.Children.Add(confirmationPane); Grid.SetRow(footer, 4); editorContent.Children.Add(footer); UpdateEditorMetrics();
    }
    private void UpdateEditorMetrics()
    {
        var width = Window.GetWindow(this)?.ActualWidth ?? 1000; var narrow = width <= 768;
        editorHeader.Padding = narrow ? new Thickness(4, 8, 12, 8) : new Thickness(Math.Clamp(width * .02, 8, 28), 20, Math.Clamp(width * .03, 16, 36), 12);
        var horizontal = narrow ? 12 : Math.Clamp(width * .03, 16, 36); editorContent.Margin = new Thickness(horizontal, 0, horizontal, narrow ? 12 : 20);
        editorCrumb.Visibility = editorSeparator.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible; editorTitle.FontSize = narrow ? 20 : 28; editorTitle.LineHeight = narrow ? 28 : 36;
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
        if (!loaded && !dirty && !Busy && !importOpening && !Session.IsUpdating) await ReadDocument();
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
    private void UpdatePosition() { editorPosition.Text = L.F("行 {0} · 列 {1}", code.Line, code.Column); editorPosition.ToolTip = Ui.Bytes(editorBytes); }
    private async Task OpenEditor()
    {
        EnsureNoUpdate();
        if (saving || importing) throw new InvalidOperationException(L.T("配置操作正在进行，请稍后重试。"));
        SetEditorVisible(true); if (reading && readTask is not null) await readTask; else if (!loaded) await ReadDocument();
        if (!Session.IsUpdating) Editor.Focus();
    }
    private void SetEditorVisible(bool visible, bool animate = true)
    {
        var wasOpen = editorOpen; editorOpen = visible; var generation = ++editorTransition;
        if (visible)
        {
            editPane.Visibility = Visibility.Visible; SetPageGutter(false); SetPagePadding(new Thickness(0)); SetHeaderVisible(false); SetBody(editPane); UpdateEditorMetrics();
            editPane.RenderTransformOrigin = new Point(.5, .3); var scale = new ScaleTransform(); editPane.RenderTransform = scale;
            ProxyVisuals.Tween(scale, ScaleTransform.ScaleXProperty, .94, 1, 300); ProxyVisuals.Tween(scale, ScaleTransform.ScaleYProperty, .94, 1, 300); ProxyVisuals.Tween(editPane, OpacityProperty, 0, 1, 300);
        }
        else
        {
            void Complete()
            {
                if (generation != editorTransition) return; editPane.Visibility = Visibility.Collapsed; SetPageGutter(true); SetPagePadding(null); SetHeaderVisible(true); SetScrollableBody(overview, overviewScroll); BackStateChanged?.Invoke();
            }
            if (wasOpen && animate && ProxyVisuals.Animate && IsLoaded)
            {
                var scale = editPane.RenderTransform as ScaleTransform ?? new ScaleTransform(); editPane.RenderTransform = scale;
                ProxyVisuals.Tween(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, .98, 83, new Point(1, 0), new Point(1, 1)); ProxyVisuals.Tween(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, .98, 83, new Point(1, 0), new Point(1, 1)); ProxyVisuals.Tween(editPane, OpacityProperty, editPane.Opacity, 0, 83, Complete);
            }
            else Complete();
        }
        Tools.Visibility = visible ? Visibility.Collapsed : Visibility.Visible; SetConfirmation(Confirmation.None); BackStateChanged?.Invoke();
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
            confirmationTitle.Text = L.T(value == Confirmation.Close ? "放弃修改并关闭？" : "放弃修改并重新读取？"); confirmationText.Text = L.T(confirmation == Confirmation.Close ? "关闭后将丢弃尚未保存的修改。确定关闭吗？" : "重新读取会丢弃当前草稿。确定从配置文件重新读取吗？");
            confirmDiscard.Content = FluentControls.ButtonContent(value == Confirmation.Close ? "trash" : "update", value == Confirmation.Close ? "丢弃并关闭" : "丢弃并重新读取");
            L.Bind(confirmDiscard, AutomationProperties.NameProperty, value == Confirmation.Close ? "丢弃并关闭" : "丢弃并重新读取");
            var move = new TranslateTransform(); var scale = new ScaleTransform(); var motion = new TransformGroup(); motion.Children.Add(scale); motion.Children.Add(move); confirmationPane.RenderTransform = motion;
            ProxyVisuals.Tween(move, TranslateTransform.YProperty, 14, 0, 167); ProxyVisuals.Tween(scale, ScaleTransform.ScaleXProperty, .97, 1, 167); ProxyVisuals.Tween(scale, ScaleTransform.ScaleYProperty, .97, 1, 167); ProxyVisuals.Tween(confirmationPane, OpacityProperty, 0, 1, 167); Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => keepEditing.Focus()));
        }
        else if (IsLoaded)
        {
            var move = new TranslateTransform(); editorFooter.RenderTransform = move; ProxyVisuals.Tween(move, TranslateTransform.YProperty, -6, 0, 167); ProxyVisuals.Tween(editorFooter, OpacityProperty, 0, 1, 167);
        }
        code.SetHeld(value != Confirmation.None); ApplyInteractionState();
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
            saved = true; Message(apply ? IsRunning ? "配置文件已保存并重新加载。" : "配置已保存并校验，内核启动时将使用此配置。" : "配置已保存，可以稍后应用。");
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
        Editor.IsReadOnly = !loaded || !writable || Busy || updating || confirmation != Confirmation.None; Editor.IsEnabled = loaded && !reading; code.SetAvailability(loaded, reading);
        readAgain.IsEnabled = available; discard.IsEnabled = available && dirty; closeEditor.IsEnabled = !Busy; keepEditing.IsEnabled = !Busy; confirmDiscard.IsEnabled = available;
        save.IsEnabled = available && loaded && writable && !conflict && confirmation == Confirmation.None && (dirty || reloadPending);
        saveOnly.IsEnabled = available && loaded && writable && !conflict && confirmation == Confirmation.None && dirty;
        resources.UpdateAvailability(PageToken); UpdateEditorState();
        if (updating && !updateWasActive) Message("正在安装应用更新，配置编辑与导入已暂时锁定。请等待更新完成。");
        else if (!updating && updateWasActive) Message("应用更新已结束，可以继续编辑配置。"); updateWasActive = updating;
    }
    private void UpdateEditorState()
    {
        editorPath.Text = documentPath.Length > 0 ? documentPath : L.T("配置文件"); editorPath.ToolTip = documentPath;
        editorState.Text = L.T(reading ? "正在读取…" : saving ? reloading ? "正在重载…" : "正在保存…" : !writable ? "只读" : dirty ? "有未保存的修改" : reloadPending ? "已保存，待重载" : loaded ? "YAML" : "暂不可用");
        editorState.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); editorDot.SetResourceReference(Shape.FillProperty, loaded && !conflict && writable ? "SuccessBrush" : "WarningBrush");
        editorNotice.Text = L.T(problem.Length > 0 ? problem : !writable ? "内核当前无法写入此配置文件，已以只读方式打开。可以选择和复制内容；请检查文件及所在目录的权限，调整后重新读取以恢复编辑。" : saved ? reloadPending ? "配置已保存，点击“保存并重新加载”使其生效。" : IsRunning ? "配置文件已保存并重新加载。" : "配置已保存并校验，内核启动时将使用此配置。" : "保存并重新加载会先写入配置文件，再由内核校验并应用。控制器地址和密钥等启动配置需重启内核后生效。");
        editorNotice.SetResourceReference(TextBlock.ForegroundProperty, problem.Length > 0 || saved ? "TextPrimaryBrush" : "TextSecondaryBrush"); editorNoticeFrame.SetResourceReference(Border.BackgroundProperty, problem.Length > 0 ? "WarningBackgroundBrush" : saved ? "SuccessBackgroundBrush" : "InfoBackgroundBrush"); if (problem.Length > 0 || saved) editorNoticeFrame.BorderBrush = Brushes.Transparent; else editorNoticeFrame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        var title = saving ? reloading ? "正在重载…" : "正在保存…" : reloadPending && !dirty ? "重试重新加载" : "保存并重新加载";
        save.Content = FluentControls.ButtonContent(saving ? "update" : "save", title); L.Bind(save, AutomationProperties.NameProperty, title);
        readAgain.Content = FluentControls.ButtonContent("update", loaded ? "从文件重新读取" : "重新读取"); L.Bind(readAgain, AutomationProperties.NameProperty, loaded ? "从文件重新读取" : "重新读取");
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
        if (!editorOpen) SetEditorVisible(true);
        SetConfirmation(Confirmation.Close); return false;
    }
    public async Task<bool> CanExitAsync()
    {
        if (leaveOpening) return false;
        if (Busy) { Message("正在完成配置写入，请稍候。"); return false; }
        if (!dirty) { await Session.SetDirtyAsync(false); return true; }
        if (Session.IsUpdating) { Message("正在安装应用更新，请等待更新完成后再处理配置草稿。"); return false; }
        if (Window.GetWindow(this) is not { } owner) return false;
        leaveOpening = true;
        try
        {
            var body = new StackPanel(); var hint = Ui.Label("退出会丢弃未保存内容，并停止本程序的代理连接。"); hint.LineHeight = 20; hint.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; body.Children.Add(hint);
            var actions = new Grid { Margin = new Thickness(0, 24, 0, 0) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
            var accept = new Button { Content = FluentControls.ButtonContent("close", "丢弃修改并退出"), Height = 32, Padding = new Thickness(12, 0, 12, 0) }.Id("ConfigExitDiscard"); FluentControlState.SetKind(accept, "Danger"); L.Bind(accept, AutomationProperties.NameProperty, "丢弃修改并退出");
            var cancel = new Button { Content = Ui.Label("继续编辑"), Height = 32, Padding = new Thickness(12, 0, 12, 0) }.Id("ConfigExitCancel"); L.Bind(cancel, AutomationProperties.NameProperty, "继续编辑");
            actions.Children.Add(accept); Grid.SetColumn(cancel, 2); actions.Children.Add(cancel); body.Children.Add(actions);
            var exiting = false; var dialog = new FluentDialog(owner, "退出前还有未保存的修改", body, []).Id("ConfigExitDialog"); dialog.CanDismiss = () => !exiting;
            cancel.Click += (_, _) => dialog.Complete(false);
            accept.Click += async (_, _) =>
            {
                if (exiting || Busy || Session.IsUpdating) return; exiting = true; accept.IsEnabled = cancel.IsEnabled = false;
                var loading = new StackPanel { Orientation = Orientation.Horizontal }; loading.Children.Add(new FluentProgressRing { Width = 16, Height = 16, IsActive = true, IsButtonSpinner = true, Margin = new Thickness(0, 0, 8, 0) }); loading.Children.Add(Ui.Label("丢弃修改并退出")); loading.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush"); accept.Content = loading;
                try { await ResetDraft(); SetEditorVisible(false, false); dialog.Complete(true); }
                catch (Exception error) { Session.Notify(error.Message, true); exiting = false; accept.IsEnabled = cancel.IsEnabled = true; accept.Content = FluentControls.ButtonContent("close", "丢弃修改并退出"); }
            };
            dialog.Loaded += (_, _) => cancel.Focus();
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (Busy || Session.IsUpdating) return false;
            return dialog.ShowDialog() == true;
        }
        finally { leaveOpening = false; }
    }
    private Task ChooseFile() => OpenImport(new ConfigImportSeed("file"));
    private static bool IsYaml(string file) => Path.GetExtension(file).Equals(".yaml", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file).Equals(".yml", StringComparison.OrdinalIgnoreCase);
    private async Task ImportFile(string file)
    {
        EnsureAvailable(); if (!IsYaml(file)) throw new InvalidOperationException(L.T("请导入 .yaml 或 .yml 配置文件。")); importing = true; ApplyInteractionState(); ConfigImportSeed seed;
        try
        {
            seed = await ConfigImportDialog.ReadFileAsync(file, maxBytes);
        }
        catch (DesktopException error) { throw EditorFailure(error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException) { throw new InvalidOperationException(L.T("无法读取配置文件，请确认文件可访问且使用 UTF-8 编码。")); }
        finally { importing = false; ApplyInteractionState(); }
        await OpenImport(seed);
    }
    private async Task ImportUrl()
    {
        if (await OpenImport(new ConfigImportSeed("url", Url: importUrl.Text.Trim()))) importUrl.Clear();
    }
    private Task Paste() => OpenImport(new ConfigImportSeed("text"));
    private async Task<bool> OpenImport(ConfigImportSeed seed)
    {
        if (importOpening) return false; EnsureAvailable(); importOpening = true; var imported = false;
        try
        {
            if (!await CanLeaveAsync()) return false; EnsureAvailable();
            if (Window.GetWindow(this) is not { } owner) return false;
            importing = true; ApplyInteractionState();
            await ConfigImportDialog.ShowAsync(owner, Session, seed, maxBytes, (document, applied) =>
            {
                ApplyDocument(ValidateDocument(document), true); reloadPending = !applied; saved = true; imported = true;
                Message(applied ? IsRunning ? "配置已保存并应用。" : "配置已保存并校验，内核启动时将使用此配置。" : "配置已保存，可以稍后应用。"); ApplyInteractionState();
            });
            return imported;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw EditorFailure(error); }
        finally { importing = false; importOpening = false; ApplyInteractionState(); }
    }
    public override async Task NavigateToAsync(NavigationTarget target)
    {
        if (target.Action != "edit" && reading && readTask is not null) await readTask;
        if (target.Action == "edit") await OpenEditor();
        else if (target.Action == "import-file" && target.Target is not null) await ImportFile(target.Target);
        else if (target.Action == "import") await ChooseFile();
        else if (target.Target == "set-resources")
        {
            if (CanGoBack && !await CanLeaveAsync()) return; SetEditorVisible(false, false);
            await Dispatcher.InvokeAsync(() => { resourcesTarget.BringIntoView(); resourcesTarget.Focus(); resourcesTarget.SetResourceReference(Border.BorderBrushProperty, "AccentBrush"); FluentMotion.Flash(resourcesTarget); }, DispatcherPriority.Loaded);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) }; timer.Tick += (_, _) => { timer.Stop(); resourcesTarget.BorderBrush = Brushes.Transparent; }; timer.Start();
        }
    }
}
