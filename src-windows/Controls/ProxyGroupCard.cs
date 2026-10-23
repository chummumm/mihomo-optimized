using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal sealed class ProxyGroupCard : Border
{
    internal ProxyExpander Expander { get; } = new();
    private readonly Grid header = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border headerPadding;
    private readonly Grid identity = new();
    private readonly FluentIcon icon = new("nav-proxies", 20);
    private readonly TextBlock title = Ui.Text("", 14);
    private readonly SlidingProxyText current = new();
    private readonly TextBlock via = Ui.Muted("", 12);
    private readonly StackPanel meta = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock type = Ui.Muted("", 12);
    private readonly TextBlock count = Ui.Muted("", 12);
    private readonly TextBlock latency = Ui.Muted("", 12);
    private readonly Ellipse marker = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button test;
    private readonly TextBlock note = Ui.Hint("", 12);
    private readonly Border well;
    private readonly ProxyMemberGrid members = new();
    private readonly FluentIcon caret = new("chevron-down", 12);
    private bool compact;
    internal event Action<bool>? OpenChanged;
    internal ProxyGroupCard(string group, Action<string> select, Action measure)
    {
        CornerRadius = new CornerRadius(4); BorderBrush = Brushes.Transparent; BorderThickness = new Thickness(0); Margin = new Thickness(0, 0, 0, 4); Child = Expander;
        Expander.Id("ProxyGroup_" + group); AutomationProperties.SetName(Expander, group);
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) }); identity.ColumnDefinitions.Add(new ColumnDefinition()); identity.VerticalAlignment = VerticalAlignment.Center; identity.Margin = new Thickness(0, 0, 12, 0);
        var iconWell = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(4), Child = icon, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        iconWell.SetResourceReference(BackgroundProperty, "ControlAltBrush"); icon.SetResourceReference(Control.ForegroundProperty, "AccentLinkBrush"); identity.Children.Add(iconWell);
        title.LineHeight = 20; title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        var now = new CurrentProxyLine(); now.Children.Add(current);
        via.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); via.TextWrapping = TextWrapping.NoWrap; via.TextTrimming = TextTrimming.CharacterEllipsis; via.LineHeight = 16; via.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; now.Children.Add(via);
        var copy = Ui.Column(title, now); Grid.SetColumn(copy, 1); identity.Children.Add(copy); ConnectedNavigation.SetTarget(identity, group); header.Children.Add(identity);
        var typeBadge = new Border { Child = type, Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }; typeBadge.SetResourceReference(BackgroundProperty, "ControlAltBrush"); meta.Children.Add(typeBadge);
        count.Margin = new Thickness(0, 0, 12, 0); count.VerticalAlignment = VerticalAlignment.Center; meta.Children.Add(count);
        var reading = new StackPanel { Orientation = Orientation.Horizontal, MinWidth = 64, VerticalAlignment = VerticalAlignment.Center }; latency.FontFamily = new FontFamily("Cascadia Mono, Consolas"); reading.Children.Add(marker); reading.Children.Add(latency); meta.Children.Add(reading);
        meta.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(meta, 1); header.Children.Add(meta);
        test = FluentControls.IconButton("speed", "测速", "MeasureGroup_" + group); test.Margin = new Thickness(0, 0, 12, 0); test.VerticalAlignment = VerticalAlignment.Center; test.Click += (_, _) => measure(); Grid.SetColumn(test, 2); header.Children.Add(test);
        var caretSlot = new Grid { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false }; caretSlot.Children.Add(caret); caret.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush"); Grid.SetColumn(caretSlot, 3); header.Children.Add(caretSlot);
        headerPadding = new Border { Child = header, Padding = new Thickness(16, 10, 8, 10), MinHeight = 68 }; Expander.Header = headerPadding; Expander.SetCaret(caret);
        note.Margin = new Thickness(0, 0, 0, 12); members.Picked += select;
        well = new Border { Child = Ui.Column(note, members), Padding = new Thickness(16), BorderThickness = new Thickness(0, 1, 0, 0) }; well.SetResourceReference(BackgroundProperty, "SubtleSurfaceBrush"); well.SetResourceReference(BorderBrushProperty, "DividerBrush"); Expander.Content = well;
        Expander.Expanded += (_, _) => OpenChanged?.Invoke(true); Expander.Collapsed += (_, _) => OpenChanged?.Invoke(false);
        SizeChanged += (_, _) => Resize();
    }
    internal void Update(ProxyNodeInfo group, IReadOnlyDictionary<string, ProxyNodeInfo> nodes, string query, bool running, string? pending)
    {
        title.Text = group.Name; title.ToolTip = group.Name; current.Set(group.Now.Length == 0 ? "—" : group.Now);
        icon.Kind = group.Type switch { "URLTest" => "top-speed", "Fallback" => "arrow-swap", "LoadBalance" => "data-area", _ => "nav-proxies" };
        var exitName = ProxyNodeInfo.ResolveExit(group.Now, nodes); var exit = exitName is null ? null : nodes.GetValueOrDefault(exitName);
        via.Text = exitName == "DIRECT" ? L.T("直连，不经过代理") : exitName is "REJECT" or "REJECT-DROP" ? L.T("拒绝") : exitName is not null && exitName != group.Now ? "→ " + exitName : exit is null ? "" : exit.Type + (exit.Udp ? " · UDP" : ""); via.ToolTip = via.Text; via.Visibility = via.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        type.Text = group.Type == "Selector" ? L.T("手动选择") : group.Type == "URLTest" ? L.T("自动选择") : group.Type; count.Text = L.F("{0} 个出口", group.Members.Count);
        var reading = exit?.DelayLabel ?? "—"; if (latency.Text != reading) { latency.Text = reading; ProxyVisuals.Reading(latency); }
        marker.SetResourceReference(Shape.FillProperty, ProxyVisuals.Tone(exit?.Tone ?? "idle")); latency.SetResourceReference(TextBlock.ForegroundProperty, exit?.Failed == true ? "DangerBrush" : "TextSecondaryBrush");
        test.IsEnabled = running && group.Members.Any(name => nodes.GetValueOrDefault(name) is { CanTest: true, IsGroup: false });
        var noteText = group.Type switch { "URLTest" => "由延迟测试自动选择，不能手动切换。", "Fallback" => "按顺序使用第一个可用节点，不能手动切换。", "LoadBalance" => "按负载均衡策略分配到各节点，不能手动切换。", _ => "" };
        note.Text = L.T(noteText); note.Visibility = noteText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var names = group.Members.Where(name => query.Length == 0 || name == group.Now || (name + " " + nodes.GetValueOrDefault(name)?.Type).Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        members.Update(group.Name, names, nodes, group.Now, group.Type == "Selector" && running, pending);
        Resize();
    }
    private void Resize()
    {
        compact = ProxyVisuals.ContainerWidth(this) is > 0 and <= 640;
        headerPadding.Padding = compact ? new Thickness(12, 10, 8, 10) : new Thickness(16, 10, 8, 10);
        header.ColumnDefinitions[1].Width = compact ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(meta, compact ? 1 : 0); Grid.SetColumn(meta, compact ? 0 : 1); meta.Margin = compact ? new Thickness(50, 4, 0, 0) : new Thickness(0, 0, 12, 0);
        count.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; well.Padding = new Thickness(compact ? 12 : 16);
    }
    // CSS flex:none/max-width:60% keeps the current name at its natural width;
    // the explanation follows it by 6 px and takes only the remaining space.
    private sealed class CurrentProxyLine : Panel
    {
        protected override Size MeasureOverride(Size available)
        {
            var width = available.Width; Children[0].Measure(new Size(width * .6, available.Height));
            var gap = Children[1].Visibility == Visibility.Collapsed ? 0 : 6;
            Children[1].Measure(new Size(Math.Max(0, width - Children[0].DesiredSize.Width - gap), available.Height));
            return new Size(Math.Min(width, Children[0].DesiredSize.Width + gap + Children[1].DesiredSize.Width), Math.Max(Children[0].DesiredSize.Height, Children[1].DesiredSize.Height));
        }
        protected override Size ArrangeOverride(Size finalSize)
        {
            var width = Math.Min(finalSize.Width * .6, Children[0].DesiredSize.Width); var gap = Children[1].Visibility == Visibility.Collapsed ? 0 : 6;
            Children[0].Arrange(new Rect(0, 0, width, finalSize.Height)); Children[1].Arrange(new Rect(width + gap, 0, Math.Max(0, finalSize.Width - width - gap), finalSize.Height)); return finalSize;
        }
    }
    private sealed class SlidingProxyText : Grid
    {
        private TextBlock? shown;
        private string text = "";
        internal SlidingProxyText() { Height = 16; ClipToBounds = true; }
        internal void Set(string value)
        {
            if (text == value) return; var old = shown; text = value;
            shown = Ui.Muted(value, 12); shown.LineHeight = 16; shown.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; shown.TextWrapping = TextWrapping.NoWrap; shown.TextTrimming = TextTrimming.CharacterEllipsis; shown.ToolTip = value;
            if (old is not null) shown.SetResourceReference(TextBlock.ForegroundProperty, "AccentLinkBrush");
            Children.Add(shown);
            if (old is null || !IsLoaded || !ProxyVisuals.Animate) { if (old is not null) Children.Remove(old); return; }
            var leaving = new TranslateTransform(); old.RenderTransform = leaving; ProxyVisuals.Tween(leaving, TranslateTransform.YProperty, 0, -16, 250, new Point(.55, .55), new Point(0, 1)); ProxyVisuals.Tween(old, OpacityProperty, 1, 0, 250, () => Children.Remove(old));
            var arriving = new TranslateTransform(); shown.RenderTransform = arriving; ProxyVisuals.Tween(arriving, TranslateTransform.YProperty, 16, 0, 250, new Point(.55, .55), new Point(0, 1)); ProxyVisuals.Tween(shown, OpacityProperty, 0, 1, 250);
        }
    }
}

internal sealed class ProxyMemberGrid : Grid
{
    private readonly ProxyTilePanel panel = new() { PreferredWidth = 180, CompactAt = 480, RowHeight = 56 };
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    private readonly Border selection;
    private readonly TranslateTransform position = new();
    private readonly TextBlock empty = Ui.Hint("没有符合筛选条件的节点。");
    private readonly Dictionary<string, Member> cards = new(StringComparer.Ordinal);
    private string current = "";
    private bool hasPosition;
    private Rect selectionBounds;
    internal event Action<string>? Picked;
    internal ProxyMemberGrid()
    {
        selection = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Visibility = Visibility.Collapsed, RenderTransform = position };
        selection.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        var check = new FluentIcon("check", 12); check.SetResourceReference(Control.ForegroundProperty, "AccentTextBrush");
        var corner = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(0, 2, 0, 4), Child = check, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, -1, 0) }; corner.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); selection.Child = corner;
        Children.Add(panel); overlay.Children.Add(selection); Children.Add(overlay); Children.Add(empty); panel.Arranged += MoveSelection;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
            var buttons = panel.Children.OfType<Member>().ToArray(); var index = Array.FindIndex(buttons, item => item.IsKeyboardFocusWithin); if (index < 0) return;
            var next = e.Key switch { Key.Home => 0, Key.End => buttons.Length - 1, Key.Left => index - 1, Key.Right => index + 1, Key.Up => index - panel.Columns, _ => index + panel.Columns };
            buttons[Math.Clamp(next, 0, buttons.Length - 1)].Focus(); e.Handled = true;
        };
    }
    protected override Size MeasureOverride(Size constraint)
    {
        var desired = base.MeasureOverride(constraint);
        // The reference viewport has a fixed row height, 1 px padding and
        // -1 px margins. Its cards stay 56 px high while the row occupies 54 px.
        return cards.Count == 0 ? desired : new Size(desired.Width, Math.Max(0, desired.Height - 2));
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (cards.Count == 0) { Clip = null; return base.ArrangeOverride(finalSize); }
        base.ArrangeOverride(new Size(finalSize.Width, finalSize.Height + 2));
        Clip = new RectangleGeometry(new Rect(-1, -1, finalSize.Width + 2, finalSize.Height + 2));
        return finalSize;
    }
    internal void Update(string group, IReadOnlyList<string> names, IReadOnlyDictionary<string, ProxyNodeInfo> nodes, string selected, bool selectable, string? pending)
    {
        current = selected; empty.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var desired = names.ToHashSet(StringComparer.Ordinal);
        foreach (var stale in cards.Keys.Where(name => !desired.Contains(name)).ToArray()) { panel.Children.Remove(cards[stale]); cards.Remove(stale); }
        for (var index = 0; index < names.Count; index++)
        {
            var name = names[index];
            if (!cards.TryGetValue(name, out var card))
            {
                card = new Member(name).Id("Proxy_" + group + "_" + name); card.Click += (_, _) => Picked?.Invoke(name); cards[name] = card;
            }
            card.Update(nodes.GetValueOrDefault(name), name == selected, selectable, name == pending);
            var old = panel.Children.IndexOf(card); if (old != index) { if (old >= 0) panel.Children.RemoveAt(old); panel.Children.Insert(index, card); }
        }
        MoveSelection();
    }
    private void MoveSelection()
    {
        if (!cards.TryGetValue(current, out var card) || !panel.Children.Contains(card) || card.ActualWidth <= 0) { selection.Visibility = Visibility.Collapsed; return; }
        var offset = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(card); selection.Visibility = Visibility.Visible;
        var nextBounds = new Rect(offset.TopLeft, new Size(card.ActualWidth, card.ActualHeight));
        if (hasPosition && selectionBounds == nextBounds) return;
        if (!hasPosition || !IsLoaded || !ProxyVisuals.Animate) { position.X = offset.X; position.Y = offset.Y; selection.Width = card.ActualWidth; selection.Height = card.ActualHeight; }
        else
        {
            ProxyVisuals.Spring(position, TranslateTransform.XProperty, offset.X, "glide"); ProxyVisuals.Spring(position, TranslateTransform.YProperty, offset.Y, "glide");
            ProxyVisuals.Spring(selection, WidthProperty, card.ActualWidth, "glide"); ProxyVisuals.Spring(selection, HeightProperty, card.ActualHeight, "glide");
        }
        selectionBounds = nextBounds; hasPosition = true;
    }
    private sealed class Member : Button
    {
        private bool wasPending;
        private readonly TextBlock title;
        private readonly TextBlock kind = Ui.Muted("", 12);
        private readonly TextBlock reading = Ui.Muted("", 12);
        private readonly Ellipse dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly Rectangle pendingBorder = new() { StrokeThickness = 1, RadiusX = 4, RadiusY = 4, StrokeDashArray = new DoubleCollection { 3, 3 }, Margin = new Thickness(-12, -8, -12, -8), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        internal Member(string name)
        {
            Style = ProxyVisuals.Style("Chip"); Padding = new Thickness(12, 8, 12, 8); MinWidth = 0; Height = 56;
            title = Ui.Text(name, 14); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.LineHeight = 20; title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            var first = new DockPanel { Margin = new Thickness(0, 0, 0, 4) }; var region = Region(name);
            if (region is not null)
            {
                var label = Ui.Muted(region, 11); label.FontWeight = FontWeights.SemiBold; label.FontFamily = new FontFamily("Cascadia Mono, Consolas"); label.HorizontalAlignment = HorizontalAlignment.Center;
                var badge = new Border { Child = label, MinWidth = 26, Height = 18, Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 8, 0) }; badge.SetResourceReference(Border.BackgroundProperty, "SubtleHoverBrush"); DockPanel.SetDock(badge, Dock.Left); first.Children.Add(badge);
            }
            first.Children.Add(title);
            var second = new DockPanel { Height = 16 }; var number = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) }; number.Children.Add(dot); reading.FontFamily = new FontFamily("Cascadia Mono, Consolas"); number.Children.Add(reading); DockPanel.SetDock(number, Dock.Right); second.Children.Add(number);
            kind.TextWrapping = TextWrapping.NoWrap; kind.TextTrimming = TextTrimming.CharacterEllipsis; second.Children.Add(kind);
            var grid = new Grid(); grid.Children.Add(Ui.Column(first, second)); pendingBorder.SetResourceReference(Shape.StrokeProperty, "AccentBrush"); grid.Children.Add(pendingBorder); Content = grid;
            ToolTip = name; AutomationProperties.SetName(this, name); ProxyVisuals.Press(this, .97); ProxyVisuals.Surface(this, "ControlHoverBrush", "ControlPressedBrush");
        }
        internal void Update(ProxyNodeInfo? node, bool selected, bool selectable, bool pending)
        {
            kind.Text = node?.Name is "DIRECT" or "REJECT" or "PASS" ? "" : node?.Type ?? ""; var next = node?.DelayLabel ?? "—";
            if (reading.Text != next) { reading.Text = next; ProxyVisuals.Reading(reading); }
            dot.SetResourceReference(Shape.FillProperty, pending ? "AccentBrush" : ProxyVisuals.Tone(node?.Tone ?? "idle"));
            if (pending && !wasPending && ProxyVisuals.Animate) dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, .3, TimeSpan.FromMilliseconds(450)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever }); else if (!pending) { dot.BeginAnimation(OpacityProperty, null); dot.Opacity = 1; } wasPending = pending;
            reading.SetResourceReference(TextBlock.ForegroundProperty, node?.Failed == true ? "DangerBrush" : "TextSecondaryBrush"); title.SetResourceReference(TextBlock.ForegroundProperty, node?.Failed == true && !selected ? "TextSecondaryBrush" : "TextPrimaryBrush");
            if (selected) BorderBrush = Brushes.Transparent; else SetResourceReference(BorderBrushProperty, "ControlStrokeBrush");
            if (!selectable) Background = Brushes.Transparent; else SetResourceReference(BackgroundProperty, "ControlBrush");
            pendingBorder.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
            IsHitTestVisible = selectable; Focusable = selectable; IsTabStop = selected; AutomationProperties.SetHelpText(this, selected ? L.T("当前节点") : node?.DelayLabel ?? "");
        }
    }
    // These fixed alternatives have no unbounded repetition. Reuse their
    // ordinary matchers: 21 static IsMatch calls exceed Regex's 15-entry cache
    // and repeatedly rebuild the symbolic automata for unlabelled node names.
    private static readonly (Regex Pattern, string Code)[] regions =
    new (string Pattern, string Code)[]
    {
        (@"香港|港[区節节]|\bHK\b|Hong\s?Kong", "HK"), (@"台湾|台灣|台北|\bTW\b|Taiwan", "TW"), (@"日本|东京|東京|大阪|\bJP\b|Japan|Tokyo|Osaka", "JP"),
        (@"新加坡|狮城|獅城|\bSG\b|Singapore", "SG"), (@"美国|美國|洛杉矶|圣何塞|硅谷|西雅图|\bUS\b|\bUSA\b|United\s?States|America|Los\s?Angeles|San\s?Jose|Seattle", "US"),
        (@"韩国|韓國|首尔|首爾|\bKR\b|Korea|Seoul", "KR"), (@"英国|英國|伦敦|\bUK\b|\bGB\b|Britain|London", "UK"), (@"德国|德國|法兰克福|\bDE\b|Germany|Frankfurt", "DE"),
        (@"法国|法國|巴黎|\bFR\b|France|Paris", "FR"), (@"荷兰|荷蘭|阿姆斯特丹|\bNL\b|Netherlands|Amsterdam", "NL"), (@"加拿大|\bCA\b|Canada", "CA"), (@"澳大利亚|澳洲|悉尼|\bAU\b|Australia|Sydney", "AU"),
        (@"俄罗斯|俄羅斯|莫斯科|\bRU\b|Russia|Moscow", "RU"), (@"印度|India|Mumbai", "IN"), (@"土耳其|\bTR\b|Turkey|Türkiye|Istanbul", "TR"), (@"马来西亚|馬來西亞|\bMY\b|Malaysia", "MY"),
        (@"泰国|泰國|\bTH\b|Thailand", "TH"), (@"越南|\bVN\b|Vietnam", "VN"), (@"菲律宾|\bPH\b|Philippines", "PH"), (@"阿根廷|\bAR\b|Argentina", "AR"), (@"巴西|\bBR\b|Brazil", "BR")
    }.Select(item => (new Regex(item.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), item.Code)).ToArray();
    private static string? Region(string name)
    {
        var runes = name.EnumerateRunes().Select(rune => rune.Value).ToArray();
        for (var index = 1; index < runes.Length; index++) if (runes[index - 1] is >= 0x1F1E6 and <= 0x1F1FF && runes[index] is >= 0x1F1E6 and <= 0x1F1FF)
        { var value = new string([(char)(runes[index - 1] - 0x1F1E6 + 65), (char)(runes[index] - 0x1F1E6 + 65)]); return value == "GB" ? "UK" : value; }
        return regions.FirstOrDefault(item => item.Pattern.IsMatch(name)).Code;
    }
}
