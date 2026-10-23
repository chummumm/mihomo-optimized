using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class ConnectionsPage : NativePage
{
    private readonly ObservableCollection<ConnectionRow> rows = [];
    private readonly Dictionary<string, ConnectionRow> byId = new(StringComparer.Ordinal);
    private readonly DataGrid table = Ui.Table(("域名", nameof(ConnectionRow.Host), 260), ("网络", nameof(ConnectionRow.Network), 80), ("类型", nameof(ConnectionRow.Type), 100), ("节点链", nameof(ConnectionRow.Chain), 200), ("规则", nameof(ConnectionRow.Rule), 140), ("进程", nameof(ConnectionRow.Process), 100), ("速率", nameof(ConnectionRow.Rate), 200), ("上传", nameof(ConnectionRow.Upload), 100), ("下载", nameof(ConnectionRow.Download), 100), ("来源 IP", nameof(ConnectionRow.SourceAddress), 140), ("连接时间", nameof(ConnectionRow.Started), 120));
    private readonly TextBox search = Ui.SearchBox("筛选域名、进程、来源、规则或出口", "ConnectionSearch");
    private readonly TrafficChart chart = new() { Height = double.NaN, MinHeight = 0, ShowNowLabel = true };
    private readonly ConnectionRateReading down = new(), up = new();
    private readonly ConnectionTotal totalDown = new(false), totalUp = new(true);
    private readonly DataGrid aggregates = Ui.Table(("分组", nameof(AggregateRow.Name), -2), ("连接", nameof(AggregateRow.Count), 90), ("上传", nameof(AggregateRow.Upload), -1), ("下载", nameof(AggregateRow.Download), -1));
    private readonly ComboBox groupBy = new() { Width = 120, Height = 32 };
    private readonly WrapPanel summary = new();
    private readonly CheckBox keepClosed = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
    private readonly Queue<string> closedOrder = new();
    private readonly HashSet<string> animatedRows = new(StringComparer.Ordinal);
    private int discarded;
    private readonly ICollectionView view;
    private readonly Button closeAll;
    private readonly MenuItem closeSelected;
    private readonly MenuItem groupMenu;
    private readonly Grid layout = new();
    private readonly Grid top = new();
    private readonly Grid rates = new();
    private readonly Border ratesCard, sourcesCard, totalsFrame, tableCard;
    private readonly Grid ratesRow = new();
    private readonly StackPanel legend = new() { Orientation = Orientation.Horizontal };
    private readonly Grid sourceLayout = new();
    private readonly StackPanel sourceHeading = new() { Orientation = Orientation.Horizontal };
    private readonly ConnectionSources sourceButtons = new();
    private readonly Border historyNote;
    private readonly TextBlock historyText = Ui.Text("", 13);
    private readonly Grid tableStage = new();
    private readonly Grid groupStage = new() { Visibility = Visibility.Collapsed };
    private readonly Grid empty = new() { IsHitTestVisible = false, Margin = new Thickness(0, 34, 0, 0) };
    private readonly ConnectionDrawer drawer = new();
    private ConnectionDrawerSheet? sheet;
    private ConnectionRow? detailRow;
    private bool paused, applyingSelection, hasSnapshot, suppressFresh;
    private long sampledAt;
    private string controller = "";
    private string sourceFilter = "";
    private double uploadTotal, downloadTotal;
    private readonly Dictionary<string, string> policyColors = new(StringComparer.Ordinal);
    private readonly ContextMenu menu = new();
    private long anchorGeneration;

    public ConnectionsPage(AppSession session) : base(session, "connections", "连接", "")
    {
        SetHeaderMeta(summary); SetHeaderBottomMargin(12);
        L.Bind(keepClosed, ContentControl.ContentProperty, "保留关闭连接"); keepClosed.Id("KeepClosedConnections"); keepClosed.Checked += (_, _) => UpdateHistory(); keepClosed.Unchecked += (_, _) => ClearClosed(); Tools.Children.Add(keepClosed);
        closeAll = Action("全部断开", "CloseAllConnections", async () =>
        {
            if (Window.GetWindow(this) is { } owner && await FluentDialog.ConfirmAsync(owner, "警告", "将会关闭所有连接", "全部断开", true)) await Session.ApiAsync(HttpMethod.Delete, "connections");
        });
        closeAll.Margin = new Thickness(0); closeAll.Content = FluentControls.ButtonContent("close-all", "全部断开"); if (closeAll.Content is Panel content && content.Children[0] is FluentIcon glyph) glyph.SetResourceReference(Control.ForegroundProperty, "DangerBrush"); Tools.Children.Add(closeAll);
        view = CollectionViewSource.GetDefaultView(rows); table.ItemsSource = view; table.Id("ConnectionTable");
        view.Filter = item => item is ConnectionRow row && (sourceFilter.Length == 0 || row.SourceKey == sourceFilter) && row.Search.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        ConfigureTable();
        var refresh = Menu("刷新", "RefreshConnections", RefreshAsync); menu.Items.Add(refresh);
        var searchMenu = new MenuItem { Header = Ui.Column(Ui.Hint("筛选连接"), search), StaysOpenOnClick = true, Focusable = false }; search.Width = 300; search.Margin = new Thickness(0, 4, 0, 0); menu.Items.Add(searchMenu);
        search.KeyDown += (_, e) => { if (e.Key == Key.Escape) { search.Clear(); menu.IsOpen = false; table.Focus(); e.Handled = true; } };
        search.TextChanged += (_, _) => { view.Refresh(); UpdateCount(); UpdateEmpty(); };
        var pause = new CheckBox().Id("PauseConnections"); L.Bind(pause, ContentControl.ContentProperty, "暂停列表"); var pauseItem = new MenuItem { Header = pause, StaysOpenOnClick = true }; menu.Items.Add(pauseItem);
        void PauseChanged(object sender, RoutedEventArgs args) { paused = pause.IsChecked == true; sampledAt = 0; menu.IsOpen = false; }
        pause.Checked += PauseChanged; pause.Unchecked += PauseChanged;
        menu.Items.Add(new Separator());
        closeSelected = Menu("关闭所选", "CloseSelectedConnection", async () => { if (table.SelectedItem is ConnectionRow { Closed: false } row) await Session.ApiAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(row.Id)); }); menu.Items.Add(closeSelected);
        menu.Items.Add(Menu("复制连接信息", "CopyConnectionDetails", () => { if (table.SelectedItem is ConnectionRow row) Ui.Copy(row.Details); return Task.CompletedTask; }));
        menu.Items.Add(Menu("清除已关闭记录", "ClearConnectionHistoryMenu", () => { ClearClosed(); return Task.CompletedTask; }));
        groupMenu = L.Bind(new MenuItem { IsCheckable = true }.Id("ShowConnectionGroups"), HeaderedItemsControl.HeaderProperty, "流量分组");
        groupMenu.Checked += (_, _) => { drawer.Hide(false); groupStage.Visibility = Visibility.Visible; UpdateAggregates(); }; groupMenu.Unchecked += (_, _) => groupStage.Visibility = Visibility.Collapsed; menu.Items.Add(groupMenu);
        table.ContextMenu = menu; ContextMenuService.SetShowOnDisabled(table, true); menu.Opened += (_, _) => closeSelected.IsEnabled = table.SelectedItem is ConnectionRow { Closed: false } && IsRunning;
        sourceButtons.Selected += key => { sourceFilter = key; view.Refresh(); UpdateSources(); UpdateCount(); UpdateEmpty(); };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.8, GridUnitType.Star) }); top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 }); top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); top.Margin = new Thickness(0, 0, 0, 12); layout.Children.Add(top);
        rates.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); rates.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); rates.RowDefinitions.Add(new RowDefinition());
        var rateHeading = new DockPanel { Margin = new Thickness(0, 0, 0, 10) }; DockPanel.SetDock(legend, Dock.Right); legend.Children.Add(Legend("下行", "AccentBrush")); legend.Children.Add(Legend("上行", "ChartUpBrush")); rateHeading.Children.Add(legend);
        var rateTitle = new StackPanel { Orientation = Orientation.Horizontal }; rateTitle.Children.Add(Ui.Label("实时吞吐", 14, true)); var history = Ui.Hint("最近 60 秒"); history.Margin = new Thickness(12, 0, 0, 0); history.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); L.Bind(history, ToolTipProperty, "最近 60 秒的下行和上行速率，数字和提示读数实时更新，右侧光点平滑跟随最新速率"); rateTitle.Children.Add(history); rateHeading.Children.Add(rateTitle); rates.Children.Add(rateHeading);
        ratesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); ratesRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); ratesRow.ColumnDefinitions.Add(new ColumnDefinition()); ratesRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ratesRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ratesRow.Margin = new Thickness(0, 0, 0, 10);
        var download = Ui.Column(Ui.Hint("↓ 下行"), down); var upload = Ui.Column(Ui.Hint("↑ 上行"), up); down.Margin = up.Margin = new Thickness(0, 3, 0, 0); upload.Margin = new Thickness(36, 0, 0, 0); Grid.SetColumn(upload, 1); ratesRow.Children.Add(download); ratesRow.Children.Add(upload);
        var total = Ui.Column(Ui.Hint("累计"), totalDown, totalUp); totalDown.Margin = totalUp.Margin = new Thickness(0, 3, 0, 0);
        totalsFrame = new Border { Child = total, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(14, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 0) }; totalsFrame.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); Grid.SetColumn(totalsFrame, 2); ratesRow.Children.Add(totalsFrame); Grid.SetRow(ratesRow, 1); rates.Children.Add(ratesRow);
        chart.Id("ConnectionThroughput"); Grid.SetRow(chart, 2); rates.Children.Add(chart); ratesCard = Ui.Card(rates, 0); ratesCard.Margin = new Thickness(0, 0, 8, 0); ratesCard.Padding = new Thickness(20, 16, 20, 8); top.Children.Add(ratesCard);
        sourceLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); sourceLayout.RowDefinitions.Add(new RowDefinition()); sourceHeading.Margin = new Thickness(6, 0, 6, 10); sourceHeading.Children.Add(Ui.Label("来源", 14, true)); var sourceHint = Ui.Hint("按实时流量占比"); sourceHint.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); sourceHint.Margin = new Thickness(12, 0, 0, 0); sourceHeading.Children.Add(sourceHint); sourceLayout.Children.Add(sourceHeading); Grid.SetRow(sourceButtons, 1); sourceLayout.Children.Add(sourceButtons);
        sourcesCard = Ui.Card(sourceLayout, 0); sourcesCard.Margin = new Thickness(0); sourcesCard.Padding = new Thickness(12, 16, 12, 10); Grid.SetColumn(sourcesCard, 1); top.Children.Add(sourcesCard);
        var historyLayout = new DockPanel(); var clear = Action("清除已关闭记录", "ClearConnectionHistory", () => { ClearClosed(); return Task.CompletedTask; }); clear.Style = FluentControls.Style("Ghost"); clear.Content = FluentControls.ButtonContent("trash", "清除已关闭记录"); clear.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(clear, Dock.Right); historyLayout.Children.Add(clear); var info = new FluentIcon("info", 14) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; info.SetResourceReference(Control.ForegroundProperty, "AccentLinkBrush"); DockPanel.SetDock(info, Dock.Left); historyLayout.Children.Add(info); historyLayout.Children.Add(historyText);
        historyNote = Ui.Card(historyLayout, 0); historyNote.Padding = new Thickness(14, 10, 12, 10); historyNote.CornerRadius = new CornerRadius(4); historyNote.Margin = new Thickness(0, 0, 0, 12); historyNote.SetResourceReference(Border.BackgroundProperty, "InfoBackgroundBrush"); historyNote.Visibility = Visibility.Collapsed; Grid.SetRow(historyNote, 1); layout.Children.Add(historyNote);
        tableStage.Children.Add(table); var emptyCopy = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; var emptyIcon = new FluentIcon("nav-connections", 28) { Margin = new Thickness(0, 0, 0, 8) }; emptyIcon.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush"); emptyCopy.Children.Add(emptyIcon); var emptyText = Ui.Hint("当前筛选下没有连接。", 13); emptyText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); emptyCopy.Children.Add(emptyText); empty.Children.Add(emptyCopy); tableStage.Children.Add(empty);
        BuildGroups(); tableStage.Children.Add(groupStage); tableStage.Children.Add(drawer); tableStage.SizeChanged += (_, _) => tableStage.Clip = new RectangleGeometry(new Rect(tableStage.RenderSize), 7, 7); tableCard = Ui.Card(tableStage, 0); tableCard.SetResourceReference(Border.BackgroundProperty, "SolidCardBrush"); tableCard.ClipToBounds = true; tableCard.Margin = new Thickness(0); tableCard.MinHeight = 240; Grid.SetRow(tableCard, 2); layout.Children.Add(tableCard); SetBody(layout);
        drawer.Closing += () => sheet?.HideSmoke();
        drawer.Closed += () => { DetachSheet(); applyingSelection = true; table.SelectedItem = null; applyingSelection = false; detailRow = null; };
        drawer.Disconnect += async () => { if (detailRow is not { Closed: false } row) return; await RunAsync(async () => { await Session.ApiAsync(HttpMethod.Delete, "connections/" + Uri.EscapeDataString(row.Id)); row.MarkClosed(); drawer.Update(row.Snapshot); }); };
        table.SelectionChanged += (_, _) => { closeSelected.IsEnabled = table.SelectedItem is ConnectionRow { Closed: false }; if (!applyingSelection && table.SelectedItem is ConnectionRow row) ShowDetails(row); };
        table.PreviewMouseLeftButtonUp += (_, args) => { if (FindRow(args.OriginalSource as DependencyObject) is { Item: ConnectionRow row } && !drawer.IsOpen) ShowDetails(row); };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape) { if (drawer.IsOpen) { sheet?.HideSmoke(); drawer.Hide(); args.Handled = true; } else if (groupStage.Visibility == Visibility.Visible) { groupMenu.IsChecked = false; args.Handled = true; } }
            else if (args.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { menu.PlacementTarget = table; menu.IsOpen = true; search.Focus(); args.Handled = true; }
            else if (args.Key == Key.Enter && table.IsKeyboardFocusWithin && table.SelectedItem is ConnectionRow row) { ShowDetails(row); args.Handled = true; }
        };
        SizeChanged += (_, _) => UpdateMetrics(); Loaded += (_, _) => UpdateMetrics(); down.Set(0); up.Set(0); UpdateSources(); UpdateCount(); UpdateEmpty();
    }
    private MenuItem Menu(string label, string id, Func<Task> action)
    {
        var item = L.Bind(new MenuItem().Id(id), HeaderedItemsControl.HeaderProperty, label); item.Click += async (_, _) => await RunAsync(action); return item;
    }
    private static FrameworkElement Legend(string title, string brush)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 0, 0) }; var swatch = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; swatch.SetResourceReference(Border.BackgroundProperty, brush); row.Children.Add(swatch); row.Children.Add(Ui.Hint(title)); return row;
    }
    private void ConfigureTable()
    {
        table.BorderThickness = new Thickness(0); table.RowHeight = 40; table.ColumnHeaderHeight = 34; table.FontSize = 13; table.FrozenColumnCount = 1; table.CanUserReorderColumns = false; table.RowStyle = ConnectionVisuals.Style("Row"); table.CellStyle = ConnectionVisuals.Style("Cell"); table.ColumnHeaderStyle = ConnectionVisuals.Style("Header"); VirtualizingPanel.SetScrollUnit(table, ScrollUnit.Pixel); VirtualizingPanel.SetCacheLength(table, new VirtualizationCacheLength(1));
        var hostFactory = new FrameworkElementFactory(typeof(ConnectionHostCell)); hostFactory.SetBinding(ConnectionHostCell.HostProperty, new Binding(nameof(ConnectionRow.Host))); hostFactory.SetBinding(ConnectionHostCell.StateProperty, new Binding(nameof(ConnectionRow.FlowState)));
        var host = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = hostFactory }, Width = 260, MinWidth = 260, SortMemberPath = nameof(ConnectionRow.Host), CellStyle = ConnectionVisuals.Style("HostCell") }; L.Bind(host, DataGridColumn.HeaderProperty, "域名"); var hostHeader = new Style(typeof(DataGridColumnHeader), ConnectionVisuals.Style("Header")); hostHeader.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 0, 10, 0))); host.HeaderStyle = hostHeader; table.Columns[0] = host;
        var routeFactory = new FrameworkElementFactory(typeof(RouteDisplay)); routeFactory.SetBinding(RouteDisplay.HopsProperty, new Binding(nameof(ConnectionRow.Hops))); routeFactory.SetBinding(RouteDisplay.ClosedProperty, new Binding(nameof(ConnectionRow.Closed)));
        var route = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = routeFactory }, Width = 200, MinWidth = 200, SortMemberPath = nameof(ConnectionRow.Chain) }; L.Bind(route, DataGridColumn.HeaderProperty, "节点链"); table.Columns[3] = route;
        var typeFrame = new FrameworkElementFactory(typeof(Border)); typeFrame.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); typeFrame.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var typeStyle = new Style(typeof(Border)); var dns = new DataTrigger { Binding = new Binding(nameof(ConnectionRow.Type)), Value = "DNS" }; dns.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ControlAltBrush"))); dns.Setters.Add(new Setter(Border.PaddingProperty, new Thickness(6, 1, 6, 1))); typeStyle.Triggers.Add(dns); typeFrame.SetValue(FrameworkElement.StyleProperty, typeStyle);
        var typeLabel = new FrameworkElementFactory(typeof(TextBlock)); typeLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(ConnectionRow.Type))); typeLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); typeLabel.SetValue(TextBlock.FontSizeProperty, 12d); typeFrame.AppendChild(typeLabel);
        var typeColumn = new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = typeFrame }, Width = 100, MinWidth = 100, SortMemberPath = nameof(ConnectionRow.Type) }; L.Bind(typeColumn, DataGridColumn.HeaderProperty, "类型"); table.Columns[2] = typeColumn;
        foreach (var (column, index) in table.Columns.Select((column, index) => (column, index)))
        {
            column.MinWidth = column.Width.Value;
            if (column is not DataGridTextColumn text) continue;
            var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap)); style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis)); style.Setters.Add(new Setter(TextBlock.FontSizeProperty, index == 2 ? 12d : 13d)); style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center)); style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, index is 1 or 2 or 5 or >= 6 ? TextAlignment.Center : TextAlignment.Left));
            if (index == 6) { var idle = new DataTrigger { Binding = new Binding(nameof(ConnectionRow.FlowState)), Value = "open" }; idle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("TextTertiaryBrush"))); style.Triggers.Add(idle); }
            text.ElementStyle = style;
        }
        table.Columns[7].SortMemberPath = nameof(ConnectionRow.UploadBytes); table.Columns[8].SortMemberPath = nameof(ConnectionRow.DownloadBytes); table.Columns[10].SortMemberPath = nameof(ConnectionRow.StartedTimestamp); table.Columns[6].SortMemberPath = nameof(ConnectionRow.DownloadRate);
        view.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.StartedTimestamp), ListSortDirection.Descending)); table.Columns[10].SortDirection = ListSortDirection.Ascending;
        table.Sorting += (_, args) =>
        {
            args.Handled = true; var first = args.Column.SortMemberPath == nameof(ConnectionRow.DownloadRate) ? ListSortDirection.Descending : ListSortDirection.Ascending;
            ListSortDirection? next = args.Column.SortDirection is null ? first : args.Column.SortDirection == first ? first == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending : null;
            foreach (var column in table.Columns) column.SortDirection = null; args.Column.SortDirection = next;
            using (view.DeferRefresh()) { view.SortDescriptions.Clear(); if (next is { } direction) { var actual = args.Column.SortMemberPath == nameof(ConnectionRow.StartedTimestamp) ? direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending : direction; view.SortDescriptions.Add(new SortDescription(args.Column.SortMemberPath, actual)); if (args.Column.SortMemberPath == nameof(ConnectionRow.DownloadRate)) view.SortDescriptions.Add(new SortDescription(nameof(ConnectionRow.UploadRate), actual)); } }
            FindScroll(table)?.ScrollToTop();
        };
        table.LoadingRow += (_, args) =>
        {
            args.Row.BeginAnimation(OpacityProperty, null); args.Row.Opacity = 1;
            if (args.Row.Item is not ConnectionRow row || row.FreshAt == 0 || Stopwatch.GetElapsedTime(row.FreshAt).TotalSeconds > 1 || !ProxyVisuals.Animate || !animatedRows.Add(row.Id)) return;
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(560)) { BeginTime = TimeSpan.FromMilliseconds(args.Row.GetIndex() % 16 * 16), EasingFunction = FluentMotion.Decelerate }; args.Row.BeginAnimation(OpacityProperty, animation);
        };
    }
    private void BuildGroups()
    {
        groupStage.SetResourceReference(Panel.BackgroundProperty, "SolidCardBrush"); groupStage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); groupStage.RowDefinitions.Add(new RowDefinition());
        foreach (var label in new[] { "设备", "进程", "出口" }) groupBy.Items.Add(L.Bind(new ComboBoxItem(), ContentControl.ContentProperty, label)); groupBy.SelectedIndex = 0; groupBy.Id("ConnectionGroupBy");
        var heading = new DockPanel { Margin = new Thickness(12, 8, 8, 8) }; var close = FluentControls.IconButton("close", "关闭分组", "CloseConnectionGroups"); close.Click += (_, _) => groupMenu.IsChecked = false; DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close); heading.Children.Add(Ui.Row(Ui.Hint("按以下维度分组"), groupBy)); groupStage.Children.Add(heading); groupBy.SelectionChanged += (_, _) => UpdateAggregates(); aggregates.Id("ConnectionGroups"); Grid.SetRow(aggregates, 1); groupStage.Children.Add(aggregates);
        aggregates.MouseDoubleClick += (_, _) => { if (aggregates.SelectedItem is AggregateRow row) { search.Text = row.Name; groupMenu.IsChecked = false; } };
    }
    private void UpdateMetrics()
    {
        var window = Window.GetWindow(this); var width = window?.ActualWidth ?? 1200; var height = window?.ActualHeight ?? 800; var stacked = width <= 1099; var phone = width <= 768;
        SetPagePadding(new Thickness(PageHorizontalPadding, width <= 640 ? 16 : 28, PageHorizontalPadding, phone ? 12 : 24));
        top.Height = stacked ? double.NaN : height <= 760 ? 220 : 264; top.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(1.8, GridUnitType.Star); top.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star); top.ColumnDefinitions[1].MinWidth = stacked ? 0 : 280;
        top.RowDefinitions[0].Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star); top.RowDefinitions[1].Height = GridLength.Auto; Grid.SetColumn(sourcesCard, stacked ? 0 : 1); Grid.SetRow(sourcesCard, stacked ? 1 : 0);
        ratesCard.Height = stacked ? phone ? double.NaN : height <= 760 ? 196 : 230 : double.NaN; ratesCard.Margin = stacked ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 8, 0); ratesCard.Padding = phone ? new Thickness(14, 12, 14, 8) : new Thickness(20, 16, 20, 8);
        rates.RowDefinitions[2].Height = phone ? new GridLength(60) : new GridLength(1, GridUnitType.Star); ratesRow.Margin = new Thickness(0, 0, 0, phone ? 8 : 10); down.FontSize = up.FontSize = phone ? 20 : 28;
        if (ratesRow.Children[1] is FrameworkElement upload) upload.Margin = new Thickness(phone ? 22 : 36, 0, 0, 0);
        legend.Visibility = phone ? Visibility.Collapsed : Visibility.Visible; tableCard.MinHeight = phone ? 200 : 240;
        var wrapTotals = phone && ActualWidth < 550; Grid.SetColumn(totalsFrame, wrapTotals ? 0 : 2); Grid.SetRow(totalsFrame, wrapTotals ? 1 : 0); Grid.SetColumnSpan(totalsFrame, wrapTotals ? 3 : 1); totalsFrame.HorizontalAlignment = phone ? HorizontalAlignment.Left : HorizontalAlignment.Right; totalsFrame.Margin = wrapTotals ? new Thickness(0, 6, 0, 0) : new Thickness(phone ? 22 : 12, 0, 0, 0); totalsFrame.BorderThickness = new Thickness(phone ? 0 : 1, 0, 0, 0); totalsFrame.Padding = new Thickness(phone ? 0 : 14, 0, 0, 0);
        sourcesCard.Padding = stacked ? new Thickness(0) : new Thickness(12, 16, 12, 10); sourcesCard.BorderThickness = new Thickness(stacked ? 0 : 1); if (stacked) sourcesCard.Background = Brushes.Transparent; else sourcesCard.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); sourceHeading.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        sourceLayout.RowDefinitions[1].Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star); sourceButtons.SetWide(!stacked, height, width <= 480);
        if (drawer.IsOpen) PlaceDrawer(phone);
        drawer.MaxWidth = sheet is null ? Math.Max(0, tableStage.ActualWidth - 20) : double.PositiveInfinity;
    }
    private void PlaceDrawer(bool phone)
    {
        if (phone && sheet is null && AdornerLayer.GetAdornerLayer(this) is { } layer) { tableStage.Children.Remove(drawer); drawer.SetMobile(true); sheet = new ConnectionDrawerSheet(this, drawer); layer.Add(sheet); }
        else if (!phone && sheet is not null) DetachSheet();
        if (sheet is null) { drawer.SetMobile(false); drawer.MaxWidth = Math.Max(0, tableStage.ActualWidth - 20); }
        else drawer.MaxWidth = double.PositiveInfinity;
    }
    private void DetachSheet()
    {
        if (sheet is null) return; var previous = sheet; sheet = null; previous.Release(); AdornerLayer.GetAdornerLayer(this)?.Remove(previous); drawer.SetMobile(false); if (!tableStage.Children.Contains(drawer)) tableStage.Children.Add(drawer);
    }
    private void ShowDetails(ConnectionRow row)
    {
        detailRow = row; groupMenu.IsChecked = false; PlaceDrawer((Window.GetWindow(this)?.ActualWidth ?? 1200) <= 768); drawer.Show(row.Snapshot);
    }
    public override void Activate() { suppressFresh = hasSnapshot; sampledAt = 0; base.Activate(); }
    public override void Deactivate() { menu.IsOpen = false; drawer.Hide(false); DetachSheet(); base.Deactivate(); }
    public override async Task RefreshAsync()
    {
        if (!IsRunning) { Message(InactiveMessage); return; }
        var source = ControllerIdentity;
        var proxiesTask = Get("proxies"); var connectionsTask = Get("connections"); await Task.WhenAll(proxiesTask, connectionsTask);
        if (!IsActive || !IsRunning || source != ControllerIdentity) return;
        controller = source;
        var proxies = (await proxiesTask).Get("proxies"); policyColors.Clear(); int index = 0;
        foreach (var name in proxies.Get("GLOBAL").Get("all").Items().Select(item => item.Text()))
            if (name != "GLOBAL" && proxies.Get(name).Get("all").ValueKind == JsonValueKind.Array) policyColors[name] = "Line" + (index++ % 9) + "Brush";
        Apply(await connectionsTask);
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) => Task.WhenAll(
        WatchStreamAsync("connections", snapshot => { if (!paused) Apply(snapshot); }, cancellationToken),
        WatchStreamAsync("traffic", value => { var upload = value.Number("up"); var download = value.Number("down"); down.Set(download); up.Set(upload); chart.Add(upload, download); }, cancellationToken));
    protected override void OnStatusChanged()
    {
        closeAll.IsEnabled = IsRunning;
        if (!IsRunning || controller != ControllerIdentity)
        {
            controller = ControllerIdentity; sourceFilter = ""; sourceButtons.Clear(); applyingSelection = true; rows.Clear(); applyingSelection = false; byId.Clear(); closedOrder.Clear(); animatedRows.Clear(); discarded = 0; sampledAt = 0; hasSnapshot = false; suppressFresh = false; drawer.Hide(false); chart.Clear(); aggregates.ItemsSource = null; uploadTotal = downloadTotal = 0; down.Set(0); up.Set(0); totalDown.Set(0); totalUp.Set(0); UpdateCount(); UpdateSources(); UpdateEmpty(); Message(InactiveMessage);
        }
    }
    protected override void OnLanguageChanged() { foreach (var row in rows) row.Relocalize(); UpdateCount(); UpdateHistory(); UpdateAggregates(); UpdateSources(); if (detailRow is not null) drawer.Update(detailRow.Snapshot); if (!IsRunning) Message(InactiveMessage); }
    private void Apply(JsonElement snapshot)
    {
        var anchor = CaptureAnchor(); uploadTotal = snapshot.Number("uploadTotal"); downloadTotal = snapshot.Number("downloadTotal"); totalDown.Set(downloadTotal); totalUp.Set(uploadTotal);
        Message(""); var now = Stopwatch.GetTimestamp(); var elapsed = sampledAt == 0 ? 0 : Stopwatch.GetElapsedTime(sampledAt, now).TotalSeconds; sampledAt = now;
        var present = new HashSet<string>(StringComparer.Ordinal); applyingSelection = true;
        try
        {
            foreach (var item in snapshot.Get("connections").Items())
            {
                var id = item.Text("id"); if (id.Length == 0 || !present.Add(id)) continue;
                if (!byId.TryGetValue(id, out var row)) { row = new ConnectionRow(id, policyColors) { FreshAt = suppressFresh ? 0 : now }; row.Update(item, 0); byId.Add(id, row); rows.Add(row); }
                else row.Update(item, elapsed);
            }
            for (var index = rows.Count - 1; index >= 0; index--)
            {
                var row = rows[index]; if (present.Contains(row.Id) || row.Closed) continue; row.MarkClosed();
                if (keepClosed.IsChecked == true) closedOrder.Enqueue(row.Id);
                else { byId.Remove(row.Id); rows.RemoveAt(index); animatedRows.Remove(row.Id); }
            }
            while (closedOrder.Count > 5000)
            {
                var expired = closedOrder.Dequeue(); if (byId.TryGetValue(expired, out var row) && row.Closed) { rows.Remove(row); byId.Remove(expired); animatedRows.Remove(expired); discarded++; }
            }
            view.Refresh();
            if (drawer.IsOpen && detailRow is not null) table.SelectedItem = view.Contains(detailRow) ? detailRow : null;
        }
        finally { applyingSelection = false; suppressFresh = false; hasSnapshot = true; }
        closeSelected.IsEnabled = table.SelectedItem is ConnectionRow { Closed: false }; UpdateCount(); UpdateHistory(); UpdateAggregates(); UpdateSources(); UpdateEmpty(); RestoreAnchor(anchor);
        if (drawer.IsOpen && detailRow is not null) drawer.Update(detailRow.Snapshot);
    }
    private void ClearClosed()
    {
        applyingSelection = true;
        try { for (var index = rows.Count - 1; index >= 0; index--) if (rows[index].Closed) { byId.Remove(rows[index].Id); animatedRows.Remove(rows[index].Id); rows.RemoveAt(index); } closedOrder.Clear(); discarded = 0; view.Refresh(); }
        finally { applyingSelection = false; }
        UpdateCount(); UpdateHistory(); UpdateAggregates(); UpdateSources(); UpdateEmpty();
    }
    private void UpdateAggregates()
    {
        if (groupStage.Visibility != Visibility.Visible) return; var kind = groupBy.SelectedIndex;
        aggregates.ItemsSource = rows.GroupBy(row => kind == 0 ? row.SourceKey : kind == 1 ? row.Process : row.Chain, StringComparer.Ordinal)
            .Select(group => new { Name = string.IsNullOrWhiteSpace(group.Key) ? L.T("未知") : group.Key, Count = group.Count(), Upload = group.Sum(row => row.UploadBytes), Download = group.Sum(row => row.DownloadBytes) })
            .OrderByDescending(group => group.Download + group.Upload).Select(group => new AggregateRow(group.Name, group.Count, Ui.Bytes(group.Upload), Ui.Bytes(group.Download))).ToArray();
    }
    public sealed record AggregateRow(string Name, int Count, string Upload, string Download);
    private void UpdateCount()
    {
        summary.Children.Clear();
        foreach (var (number, label) in new[] { (rows.Count(row => !row.Closed), "个活动连接"), (rows.Select(row => row.SourceKey).Distinct(StringComparer.Ordinal).Count(), "个来源") })
        {
            var part = Ui.Muted("", 13); part.Margin = new Thickness(0, 0, 14, 0); var reading = new Run(number.ToString()); reading.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush"); part.Inlines.Add(reading); part.Inlines.Add(" " + L.T(label)); summary.Children.Add(part);
        }
        AutomationProperties.SetName(summary, string.Join(" · ", summary.Children.OfType<TextBlock>().Select(text => new TextRange(text.ContentStart, text.ContentEnd).Text)));
    }
    private void UpdateHistory()
    {
        var shown = keepClosed.IsChecked == true; var wasShown = historyNote.Visibility == Visibility.Visible; historyNote.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        historyText.Text = L.T("已关闭记录保留上限") + ": 5000. " + L.T("数量包含当前保留的活跃与已关闭记录。") + (discarded > 0 ? " " + L.F("已移除最早记录：{0}", discarded) : "");
        if (shown && !wasShown && IsLoaded && ProxyVisuals.Animate) { var move = new TranslateTransform(); historyNote.RenderTransform = move; ProxyVisuals.Tween(move, TranslateTransform.YProperty, 28, 0, 250); ProxyVisuals.Tween(historyNote, OpacityProperty, 0, 1, 250); }
    }
    private void UpdateEmpty() => empty.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    private void UpdateSources()
    {
        var grouped = rows.GroupBy(row => row.SourceKey, StringComparer.Ordinal).ToArray(); var keys = grouped.Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        if (sourceFilter.Length > 0 && !keys.Contains(sourceFilter)) { sourceFilter = ""; view.Refresh(); UpdateEmpty(); }
        var sources = new List<ConnectionSource> { new("", L.T("全部"), rows.Count, rows.Sum(row => row.UploadRate + row.DownloadRate)) };
        foreach (var group in grouped.OrderBy(group => group.Key == "mihomo" ? 0 : 1)) sources.Add(new ConnectionSource(group.Key, group.Key == "unknown" ? L.T("未知来源") : group.Key, group.Count(), group.Sum(row => row.UploadRate + row.DownloadRate)));
        sourceButtons.Update(sources, sourceFilter);
    }
    private sealed record ScrollAnchor(double Offset, double Horizontal, bool AtTop, bool AtBottom, (string Id, double Offset)[] Candidates);
    private ScrollAnchor? CaptureAnchor()
    {
        var scroll = FindScroll(table); if (scroll is null || table.Items.Count == 0) return null; var first = Math.Clamp((int)(scroll.VerticalOffset / 40), 0, table.Items.Count - 1);
        var candidates = Enumerable.Range(first, Math.Min(12, table.Items.Count - first)).Select(index => (((ConnectionRow)table.Items[index]).Id, scroll.VerticalOffset - index * 40)).ToArray(); return new ScrollAnchor(scroll.VerticalOffset, scroll.HorizontalOffset, scroll.VerticalOffset <= 1, scroll.ScrollableHeight > 0 && scroll.ScrollableHeight - scroll.VerticalOffset <= 2, candidates);
    }
    private void RestoreAnchor(ScrollAnchor? anchor)
    {
        if (anchor is null) return; var generation = ++anchorGeneration;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!IsActive || generation != anchorGeneration || FindScroll(table) is not { } scroll) return;
            var offset = anchor.AtTop ? 0 : anchor.AtBottom ? scroll.ScrollableHeight : anchor.Offset;
            if (!anchor.AtTop && !anchor.AtBottom) foreach (var candidate in anchor.Candidates) { if (!byId.TryGetValue(candidate.Id, out var row)) continue; var index = table.Items.IndexOf(row); if (index < 0) continue; offset = index * 40 + candidate.Offset; break; }
            scroll.ScrollToVerticalOffset(Math.Clamp(offset, 0, scroll.ScrollableHeight)); scroll.ScrollToHorizontalOffset(anchor.Horizontal);
        }, DispatcherPriority.Loaded);
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) { var child = VisualTreeHelper.GetChild(parent, index); if (child is ScrollViewer scroll) return scroll; if (FindScroll(child) is { } nested) return nested; } return null;
    }
    private static DataGridRow? FindRow(DependencyObject? item)
    {
        for (; item is not null; item = item is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item)) if (item is DataGridRow row) return row; return null;
    }

    public sealed class ConnectionRow(string id, IReadOnlyDictionary<string, string> policyColors) : INotifyPropertyChanged
    {
        public string Id { get; } = id;
        public bool Closed { get; private set; }
        internal long FreshAt { get; init; }
        public string State => Closed ? L.T("已关闭") : L.T("活动");
        public string FlowState => Closed ? "closed" : UploadRate + DownloadRate > 0 ? "flow" : "open";
        public string Host { get; private set; } = "";
        private string processName = "";
        public string SourceKey { get; private set; } = "unknown";
        public string Source => processName.Length > 0 ? processName : SourceAddress;
        public string SourceAddress { get; private set; } = "";
        public string Process => processName.Length > 0 ? processName : "-";
        public double UploadBytes { get; private set; }
        public double DownloadBytes { get; private set; }
        public double UploadRate { get; private set; }
        public double DownloadRate { get; private set; }
        public string Type { get; private set; } = "";
        private DateTimeOffset? started;
        public string Started => started is { } value ? ConnectionVisuals.RelativeTime(value) : lastData.Text("start");
        public long StartedTimestamp => started?.ToUnixTimeMilliseconds() ?? 0;
        public string Network { get; private set; } = "";
        public string Rule { get; private set; } = "";
        public string Chain { get; private set; } = "";
        public RouteHop[] Hops { get; private set; } = [];
        public string Upload { get; private set; } = "";
        public string Download { get; private set; } = "";
        public string Rate { get; private set; } = "";
        private JsonElement lastData;
        internal ConnectionDetailSnapshot Snapshot
        {
            get
            {
                var metadata = lastData.Get("metadata"); string Endpoint(string address, string port) => address.Length > 0 ? address + ":" + port : "";
                return new(Id, Closed, lastData.Bool("dns"), Network, Endpoint(metadata.Text("sourceIP"), metadata.Text("sourcePort")), Type, Rule, Chain, Hops, Host, Endpoint(metadata.Text("destinationIP"), metadata.Text("destinationPort")), started?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? lastData.Text("start"), processName, metadata.Text("processPath"), Upload, Download);
            }
        }
        public string Details
        {
            get { var metadata = lastData.Get("metadata"); return L.F("目标：{0}\n目标 IP：{1}\n嗅探域名：{2}\n进程：{3}\n来源：{4}:{5}\n网络：{6}\n入口：{7} / {8}\n规则：{9}\n出口：{10}\n建立时间：{11}\n上传：{12}\n下载：{13}\n连接 ID：{14}\n状态：{15}", Host, metadata.Text("destinationIP"), metadata.Text("sniffHost"), metadata.Text("processPath"), metadata.Text("sourceIP"), metadata.Text("sourcePort"), Network, Type, metadata.Text("inboundName"), Rule, Chain, lastData.Text("start"), Upload, Download, Id, State); }
        }
        public string Search => string.Join(' ', Host, Source, SourceAddress, SourceKey, Process, Network, Rule, Chain);
        private double previousDownload, previousUpload;
        private bool seen;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void MarkClosed() { Closed = true; UploadRate = DownloadRate = 0; Rate = "-"; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
        public void Relocalize() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        public void Update(JsonElement data, double elapsed)
        {
            lastData = data.Clone(); var wasClosed = Closed;
            var metadata = data.Get("metadata"); var process = metadata.Text("processPath"); var downloaded = data.Number("download"); Closed = false;
            Host = metadata.Text("host", fallback: metadata.Text("sniffHost", fallback: metadata.Text("destinationIP"))) + ":" + metadata.Text("destinationPort");
            var source = metadata.Text("sourceIP"); SourceAddress = source;
            var validSource = System.Net.IPAddress.TryParse(source, out var address) && !address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.IPv6Any) && !(address.IsIPv4MappedToIPv6 && address.MapToIPv4().Equals(System.Net.IPAddress.Any));
            SourceKey = data.Bool("dns") ? "mihomo" : validSource ? address!.ToString() : metadata.Text("type") == "Inner" ? "mihomo" : "unknown";
            processName = process.Length > 0 ? System.IO.Path.GetFileName(process) : "";
            UploadBytes = data.Number("upload"); DownloadBytes = downloaded; Type = data.Bool("dns") ? "DNS" : metadata.Text("type"); started = DateTimeOffset.TryParse(data.Text("start"), out var date) ? date : null; Network = metadata.Text("network").ToUpperInvariant();
            Rule = data.Bool("dns") ? "" : data.Text("rulePayload").Length > 0 ? data.Text("rule") + " :: " + data.Text("rulePayload") : data.Text("rule");
            var hops = (data.Bool("dns") ? data.Get("chains").Items().Take(1) : data.Get("chains").Items().Reverse()).Select(item => item.Text()).ToArray(); Chain = string.Join(" / ", hops);
            Hops = hops.Select(name => new RouteHop(name, name is "REJECT" or "REJECT-DROP" ? "DangerBrush" : name == "GLOBAL" ? "LineSystemBrush" : policyColors.GetValueOrDefault(name, "TextTertiaryBrush"), name is "GLOBAL" or "REJECT" or "REJECT-DROP" || policyColors.ContainsKey(name))).ToArray();
            Upload = ConnectionVisuals.Bytes(UploadBytes); Download = ConnectionVisuals.Bytes(downloaded);
            bool continuous = seen && !wasClosed && elapsed > 0 && elapsed <= 10 && downloaded >= previousDownload && UploadBytes >= previousUpload;
            UploadRate = continuous ? (UploadBytes - previousUpload) / elapsed : 0; DownloadRate = continuous ? (downloaded - previousDownload) / elapsed : 0;
            Rate = UploadRate > 0 && DownloadRate > 0 ? $"↑ {ConnectionVisuals.Bytes(UploadRate)}/s ↓ {ConnectionVisuals.Bytes(DownloadRate)}/s" : UploadRate > 0 ? $"↑ {ConnectionVisuals.Bytes(UploadRate)}/s" : DownloadRate > 0 ? $"↓ {ConnectionVisuals.Bytes(DownloadRate)}/s" : "-";
            previousDownload = downloaded; previousUpload = UploadBytes; seen = true; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }
}
