using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal sealed class ExitSummaryList : Border
{
    private readonly StackPanel list = new();
    private readonly Dictionary<string, ExitRow> rows = new(StringComparer.Ordinal);
    private readonly TextBlock empty = Ui.Hint("配置中没有策略组。");
    private readonly bool compact;
    private readonly string id;
    public event Action<ProxyExit, FrameworkElement>? Selected;
    public ExitSummaryList(string id, bool compact = false)
    {
        this.compact = compact; this.id = id; this.Id(id); L.Bind(this, AutomationProperties.NameProperty, "当前出口");
        CornerRadius = new CornerRadius(8); ClipToBounds = true;
        if (!compact) { BorderThickness = new Thickness(1); SetResourceReference(BorderBrushProperty, "CardStrokeBrush"); SetResourceReference(BackgroundProperty, "CardBrush"); }
        empty.Margin = new Thickness(20, 16, 20, 16); Child = list;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new SummaryPeer(this);
    private sealed class SummaryPeer(ExitSummaryList owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(ExitSummaryList);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
    public void SetEmptyText(string text) => empty.Text = L.T(text);
    public void Update(IEnumerable<ProxyExit> source)
    {
        var values = source.ToArray();
        var names = values.Select(item => item.Group).ToHashSet(StringComparer.Ordinal);
        foreach (string old in rows.Keys.Where(name => !names.Contains(name)).ToArray()) { list.Children.Remove(rows[old]); rows.Remove(old); }
        if (values.Length == 0) { if (!list.Children.Contains(empty)) list.Children.Add(empty); return; }
        list.Children.Remove(empty);
        for (int index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (!rows.TryGetValue(value.Group, out var row))
            {
                row = new ExitRow(compact).Id(id + "_" + value.Group); rows.Add(value.Group, row);
                row.Click += (_, _) => { if (row.Value is { } selected) Selected?.Invoke(selected, row.Identity); };
            }
            if (list.Children.IndexOf(row) != index) { list.Children.Remove(row); list.Children.Insert(index, row); }
            row.Update(value, index);
        }
    }
    private sealed class ExitRow : Button
    {
        private readonly TextBlock title = Ui.Text("");
        private readonly TextBlock detail = Ui.Muted("");
        private readonly TextBlock delay = Ui.Muted("—");
        private readonly Ellipse dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        private readonly FluentIcon icon = new("nav-proxies", 20);
        private readonly bool compact;
        public FrameworkElement Identity { get; }
        public ProxyExit? Value { get; private set; }
        public ExitRow(bool compact)
        {
            this.compact = compact;
            Style = RowStyle(compact); HorizontalContentAlignment = HorizontalAlignment.Stretch; Padding = new Thickness(compact ? 12 : 16, compact ? 8 : 10, compact ? 12 : 16, compact ? 8 : 10); MinHeight = compact ? 52 : 64;
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var identity = new Grid(); identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); identity.ColumnDefinitions.Add(new ColumnDefinition());
            icon.Width = icon.Height = compact ? 16 : 20;
            var iconBox = new Border { Width = compact ? 32 : 36, Height = compact ? 32 : 36, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, compact ? 12 : 14, 0), Child = icon };
            iconBox.SetResourceReference(BackgroundProperty, "ControlAltBrush"); icon.SetResourceReference(ForegroundProperty, "AccentLinkBrush"); identity.Children.Add(iconBox);
            title.TextWrapping = detail.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = detail.TextTrimming = TextTrimming.CharacterEllipsis;
            title.FontSize = 14; title.FontWeight = FontWeights.Normal; title.LineHeight = 20; detail.FontSize = 12; detail.LineHeight = 16;
            var labels = Ui.Column(title, detail); labels.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(labels, 1); identity.Children.Add(labels); Identity = identity; grid.Children.Add(identity);
            delay.Tabular();
            dot.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 6, 0);
            var latency = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(compact ? 12 : 16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; latency.Children.Add(dot); if (!compact) latency.Children.Add(delay); Grid.SetColumn(latency, 1); grid.Children.Add(latency);
            var chevron = new FluentIcon("chevron-right", compact ? 12 : 16) { Margin = new Thickness(compact ? 12 : 16, 0, 0, 0) }; chevron.SetResourceReference(ForegroundProperty, "TextSecondaryBrush"); Grid.SetColumn(chevron, 2); grid.Children.Add(chevron);
            Content = grid;
        }
        public void Update(ProxyExit value, int index)
        {
            Value = value; title.Text = value.Group; detail.Text = (compact ? "" : L.T(value.Manual ? "手动" : "自动") + " · ") + (value.Exit ?? "—") + (compact && value.Delay > 0 ? $" · {value.Delay:0} ms" : "");
            delay.Text = value.Delay > 0 ? $"{value.Delay:0} ms" : "—"; dot.SetResourceReference(Shape.FillProperty, value.Tone);
            icon.Kind = value.Manual ? "nav-proxies" : "top-speed";
            AutomationProperties.SetName(this, $"{value.Group} · {value.Exit ?? "—"} · {Ui.Delay(value.Delay)}");
            if (compact) { BorderThickness = new Thickness(1); Margin = new Thickness(0, index == 0 ? 0 : 4, 0, 0); SetResourceReference(BorderBrushProperty, "CardStrokeBrush"); }
            else { BorderThickness = new Thickness(0, index > 0 ? 1 : 0, 0, 0); SetResourceReference(BorderBrushProperty, "DividerBrush"); }
        }

        private static Style RowStyle(bool compact) => (Style)System.Windows.Markup.XamlReader.Parse($$"""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:controls="clr-namespace:Mihomo.Desktop.Controls;assembly=mihomo-optimized" TargetType="Button">
              <Setter Property="Background" Value="{{(compact ? "{DynamicResource CardBrush}" : "Transparent")}}"/>
              <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                <Border x:Name="Frame" CornerRadius="{{(compact ? "4" : "0")}}" controls:ShellVisuals.Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Stretch" VerticalAlignment="Center"/></Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="controls:ShellVisuals.Background" Value="{DynamicResource CardHoverBrush}"/></Trigger>
                  <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="controls:ShellVisuals.Background" Value="{DynamicResource CardSecondaryBrush}"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
    }
}
