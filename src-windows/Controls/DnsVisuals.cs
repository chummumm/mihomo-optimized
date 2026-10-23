using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>DNS-specific views from the supplied dashboard; native selection and table peers are retained.</summary>
internal static class DnsVisuals
{
    internal static readonly FontFamily Mono = new("Cascadia Mono, Cascadia Code, Consolas");
    internal const double HeaderHeight = 10 * 1.43 + 18 + 1;
    internal const double EmptyTableHeight = HeaderHeight + 13 * 1.43 + 64 + 1;
    internal static string Number(double value, int digits = 0) => value.ToString(digits == 0 ? "N0" : "#,0." + new string('#', digits), CultureInfo.CurrentCulture);
    internal static string Time(DateTimeOffset date, bool full = false)
    {
        var culture = CultureInfo.GetCultureInfo(L.ResolvedLanguage);
        string format = L.IsEnglish ? full ? "M/d/yyyy, h:mm:ss tt" : "h:mm:ss tt" : full ? "yyyy/M/d HH:mm:ss" : "HH:mm:ss";
        return date.LocalDateTime.ToString(format, culture);
    }
    internal static string Time(string value, bool full = false) => DateTimeOffset.TryParse(value, out var date) ? Time(date, full) : "—";
    internal static TextBlock Caption(string value, bool translate = true)
    {
        var text = translate ? Ui.Label(value, 12) : Ui.Text(value, 12);
        text.LineHeight = 19.2; text.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); return text;
    }
    internal static Border Card(UIElement content, Thickness? padding = null, double top = 16)
    {
        var card = new Border { Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(20, 16, 20, 16), Margin = new Thickness(0, top, 0, 0) };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush"); card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeBrush"); return card;
    }
    internal static StackPanel Inline(params UIElement[] content)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in content) row.Children.Add(item); return row;
    }
    internal static Style TableCellStyle => (Style)Templates["DnsCell"];
    internal static void Table(DataGrid table)
    {
        table.BorderThickness = new Thickness(0); table.RowHeight = double.NaN; table.MinRowHeight = 13 * 1.43 + 19; table.FontSize = 13;
        table.GridLinesVisibility = DataGridGridLinesVisibility.None; table.CanUserResizeRows = false; table.CanUserReorderColumns = false;
        table.CanUserSortColumns = false; table.ColumnHeaderStyle = (Style)Templates["DnsHeader"]; table.CellStyle = TableCellStyle;
        table.ColumnHeaderHeight = HeaderHeight;
        table.RowStyle = (Style)Templates["DnsRow"]; table.SetResourceReference(Control.BackgroundProperty, "DialogTopBrush");
        table.SetResourceReference(Control.BorderBrushProperty, "DividerBrush");
        foreach (var column in table.Columns) { column.MinWidth = 0; column.CanUserResize = false; column.Width = DataGridLength.Auto; }
        foreach (var column in table.Columns.OfType<DataGridTextColumn>()) column.ElementStyle = TextStyle(false);
    }
    internal static Style TextStyle(bool mono, double size = 13, bool strong = false, bool wrap = false)
    {
        var style = new Style(typeof(TextBlock)); style.Setters.Add(new Setter(TextBlock.FontSizeProperty, size));
        style.Setters.Add(new Setter(TextBlock.LineHeightProperty, size * 1.43));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        if (mono) style.Setters.Add(new Setter(TextBlock.FontFamilyProperty, Mono));
        if (strong) style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold)); return style;
    }
    internal static void Input(Control input)
    {
        if (input is ComboBox) input.Style = (Style)Templates["DnsSelect"];
        input.MinWidth = 0; input.MinHeight = 0; input.FontSize = 13; input.FontWeight = FontWeights.Normal; input.Height = 32; input.Padding = new Thickness(10, 0, 10, 0);
        input.FocusVisualStyle = null; input.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        input.SetResourceReference(Control.BackgroundProperty, "CardBrush"); input.SetResourceReference(Control.BorderBrushProperty, "ControlStrongBrush");
        if (input is TextBox) input.Template = (ControlTemplate)Templates["DnsInputTemplate"];
    }
    internal static TextBox InputBox(string hint, string id)
    {
        var input = new TextBox { VerticalContentAlignment = VerticalAlignment.Center }.Id(id);
        L.Bind(input, FrameworkElement.TagProperty, hint); L.Bind(input, FrameworkElement.ToolTipProperty, hint); L.Bind(input, AutomationProperties.NameProperty, hint);
        return input;
    }
    // HTML's automatic table layout uses intrinsic cell widths. Preserve that
    // behavior without fixed per-column widths or losing the native grid peers.
    internal static void FitColumns(DataGrid table, IReadOnlyList<double> contentWidths)
    {
        if (table.Columns.Count == 0 || table.ActualWidth <= 0) return;
        double available = table.ActualWidth;
        if (FindVerticalBar(table) is { Visibility: Visibility.Visible } bar) available -= bar.ActualWidth;
        var widths = table.Columns.Select((column, index) => Math.Max(
            TextWidth(table, column.Header?.ToString() ?? "", true, 10, FontWeights.Medium, .6),
            index < contentWidths.Count ? contentWidths[index] : 0) + 28).ToArray();
        double natural = widths.Sum(), factor = natural > 0 ? Math.Max(1, available / natural) : 1;
        for (int index = 0; index < widths.Length; index++)
        {
            double width = widths[index] * factor;
            if (!table.Columns[index].Width.IsAbsolute || Math.Abs(table.Columns[index].Width.Value - width) > .1)
                table.Columns[index].Width = new DataGridLength(width);
        }
        static ScrollBar? FindVerticalBar(DependencyObject item)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
            {
                var child = VisualTreeHelper.GetChild(item, index);
                if (child is ScrollBar { Orientation: Orientation.Vertical } result) return result;
                if (FindVerticalBar(child) is { } nested) return nested;
            }
            return null;
        }
    }
    internal static double TextWidth(FrameworkElement owner, string value, bool mono = false, double size = 13, FontWeight? weight = null, double spacing = 0)
    {
        var family = mono ? Mono : owner.TryFindResource("BodyFont") as FontFamily ?? new FontFamily("Segoe UI");
        var text = new FormattedText(value, CultureInfo.GetCultureInfo(L.ResolvedLanguage), FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, VisualTreeHelper.GetDpi(owner).PixelsPerDip);
        return text.WidthIncludingTrailingWhitespace + spacing * value.Length;
    }
    internal static void SmallButton(Button button, string? glyph = null, bool trailing = false)
    {
        button.Height = 28; button.MinHeight = 28; button.FontSize = 12; button.Padding = new Thickness(10, 0, 10, 0); button.Margin = new Thickness(0);
        if (glyph is null) return;
        var text = new TextBlock { FontSize = 12, FontWeight = FontWeights.Normal, LineHeight = 17.16, VerticalAlignment = VerticalAlignment.Center };
        var binding = BindingOperations.GetBinding(button, ContentControl.ContentProperty);
        if (binding is not null) { BindingOperations.ClearBinding(button, ContentControl.ContentProperty); text.SetBinding(TextBlock.TextProperty, binding); }
        else text.Text = button.Content?.ToString() ?? "";
        var icon = new FluentIcon { Kind = glyph, Width = 14, Height = 14, Margin = trailing ? new Thickness(6, 0, 0, 0) : new Thickness(0, 0, 6, 0) };
        button.Content = trailing ? Inline(text, icon) : Inline(icon, text);
    }
    internal static readonly ResourceDictionary Templates = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          xmlns:c="clr-namespace:Mihomo.Desktop.Controls;assembly=mihomo-optimized">
          <ControlTemplate x:Key="DnsInputTemplate" TargetType="TextBox">
            <Grid>
              <Border x:Name="Halo" Margin="-3" CornerRadius="7" BorderThickness="3" BorderBrush="{DynamicResource AccentBrush}" Opacity=".16" Visibility="Hidden" IsHitTestVisible="False"/>
              <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="4"><Grid>
                <ScrollViewer x:Name="PART_ContentHost" VerticalAlignment="Center"/>
                <TextBlock x:Name="Hint" Text="{TemplateBinding Tag}" Margin="{TemplateBinding Padding}" FontFamily="{TemplateBinding FontFamily}" FontSize="{TemplateBinding FontSize}" FontWeight="Normal" Foreground="{DynamicResource TextTertiaryBrush}" TextAlignment="{TemplateBinding TextAlignment}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" Visibility="Collapsed" IsHitTestVisible="False"/>
              </Grid></Border>
              <Border x:Name="Hover" BorderBrush="{DynamicResource AccentBrush}" Opacity=".45" BorderThickness="1" CornerRadius="4" Visibility="Hidden" IsHitTestVisible="False"/>
            </Grid>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="Transparent"/><Setter TargetName="Hover" Property="Visibility" Value="Visible"/></Trigger>
              <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Hover" Property="Visibility" Value="Hidden"/><Setter TargetName="Halo" Property="Visibility" Value="Visible"/></Trigger>
              <Trigger Property="Text" Value=""><Setter TargetName="Hint" Property="Visibility" Value="Visible"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".45"/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
          <Style x:Key="DnsSelect" TargetType="ComboBox">
            <Setter Property="FontFamily" Value="{DynamicResource BodyFont}"/><Setter Property="FontWeight" Value="Normal"/><Setter Property="MaxDropDownHeight" Value="320"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="ItemContainerStyle"><Setter.Value><Style TargetType="ComboBoxItem"><Setter Property="FontFamily" Value="{DynamicResource BodyFont}"/><Setter Property="FontWeight" Value="Normal"/><Setter Property="FontSize" Value="13"/></Style></Setter.Value></Setter>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
              <Grid>
                <Border x:Name="Halo" Margin="-3" CornerRadius="7" BorderThickness="3" BorderBrush="{DynamicResource AccentBrush}" Opacity=".16" Visibility="Hidden" IsHitTestVisible="False"/>
                <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="4">
                  <Grid><TextBlock Text="{TemplateBinding SelectionBoxItem}" FontFamily="{TemplateBinding FontFamily}" FontSize="{TemplateBinding FontSize}" FontWeight="Normal" Foreground="{TemplateBinding Foreground}" Margin="10,0,28,0" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                    <Path Data="M0,0 L5,5 L10,0Z" Fill="{DynamicResource TextTertiaryBrush}" Width="10" Height="5" Stretch="Fill" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,9,0"/>
                  </Grid>
                </Border>
                <Border x:Name="Hover" BorderBrush="{DynamicResource AccentBrush}" Opacity=".45" BorderThickness="1" CornerRadius="4" Visibility="Hidden" IsHitTestVisible="False"/>
                <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" ClickMode="Press"><ToggleButton.Style><Style TargetType="ToggleButton"><Setter Property="Template"><Setter.Value><ControlTemplate><Border Background="Transparent"/></ControlTemplate></Setter.Value></Setter></Style></ToggleButton.Style></ToggleButton>
                <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="None">
                  <Border Background="{DynamicResource PanelSolidBrush}" BorderBrush="{DynamicResource ControlStrongBrush}" BorderThickness="1" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="{TemplateBinding MaxDropDownHeight}"><ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border>
                </Popup>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="Transparent"/><Setter TargetName="Hover" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Hover" Property="Visibility" Value="Hidden"/><Setter TargetName="Halo" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="DnsHeader" TargetType="DataGridColumnHeader">
            <Setter Property="Padding" Value="14,9"/><Setter Property="FontSize" Value="10"/><Setter Property="FontWeight" Value="Medium"/>
            <Setter Property="FontFamily" Value="Cascadia Mono, Cascadia Code, Consolas"/><Setter Property="Foreground" Value="{DynamicResource TextDisabledBrush}"/>
            <Setter Property="Background" Value="{DynamicResource DialogTopBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource DividerBrush}"/>
            <Setter Property="BorderThickness" Value="0,0,0,1"/><Setter Property="HorizontalContentAlignment" Value="Left"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="DataGridColumnHeader"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"><TextBlock Text="{TemplateBinding Content}" FontFamily="{TemplateBinding FontFamily}" FontSize="{TemplateBinding FontSize}" FontWeight="{TemplateBinding FontWeight}" Foreground="{TemplateBinding Foreground}" LineHeight="14.3" TextWrapping="NoWrap"/></Border></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="DnsCell" TargetType="DataGridCell">
            <Setter Property="Padding" Value="14,9"/><Setter Property="BorderThickness" Value="0,0,0,1"/><Setter Property="BorderBrush" Value="{DynamicResource DividerBrush}"/>
            <Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="DataGridCell"><Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"><ContentPresenter VerticalAlignment="Center"/></Border></ControlTemplate></Setter.Value></Setter>
            <Style.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="Background" Value="{DynamicResource SubtleHoverBrush}"/></Trigger></Style.Triggers>
          </Style>
          <Style x:Key="DnsRow" TargetType="DataGridRow">
            <Setter Property="Background" Value="Transparent"/><Setter Property="BorderThickness" Value="0"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Style.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource SubtleHoverBrush}"/></Trigger></Style.Triggers>
          </Style>
          <Style x:Key="DnsLive" TargetType="CheckBox">
            <Setter Property="Height" Value="32"/><Setter Property="Padding" Value="12,0"/><Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/><Setter Property="Background" Value="{DynamicResource ControlBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource ControlStrokeBrush}"/><Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
              <Border x:Name="Frame" CornerRadius="4" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" Padding="{TemplateBinding Padding}"><ContentPresenter VerticalAlignment="Center"/></Border>
              <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource ControlHoverBrush}"/></Trigger>
              <Trigger Property="IsChecked" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource ControlPressedBrush}"/></Trigger>
              <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource TextDisabledBrush}"/></Trigger>
              <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource FocusOuterBrush}"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
}

/// <summary>CSS flex-wrap with explicit horizontal and vertical gaps, without trailing item margins.</summary>
internal class DnsFlowPanel : Panel
{
    internal double Gap { get; set; } = 12;
    internal double RowGap { get; set; } = 8;
    internal bool SpaceBetween { get; set; }
    internal bool AlignBottom { get; set; }
    internal bool AlignTop { get; set; }
    internal bool ForceColumn { get; set; }
    private readonly List<List<UIElement>> lines = [];
    protected override Size MeasureOverride(Size available)
    {
        lines.Clear(); var line = new List<UIElement>(); double used = 0, maxWidth = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            if (line.Count > 0 && (ForceColumn || used + Gap + child.DesiredSize.Width > available.Width)) Finish();
            used += (line.Count == 0 ? 0 : Gap) + child.DesiredSize.Width; line.Add(child);
        }
        if (line.Count > 0) Finish();
        return new Size(double.IsInfinity(available.Width) ? maxWidth : Math.Min(available.Width, maxWidth), height);
        void Finish() { if (lines.Count > 0) height += RowGap; height += line.Max(item => item.DesiredSize.Height); maxWidth = Math.Max(maxWidth, used); lines.Add(line); line = []; used = 0; }
    }
    protected override Size ArrangeOverride(Size final)
    {
        double y = 0;
        foreach (var line in lines)
        {
            double height = line.Max(item => item.DesiredSize.Height), x = 0;
            double gap = SpaceBetween && line.Count > 1 ? Math.Max(Gap, (final.Width - line.Sum(item => item.DesiredSize.Width)) / (line.Count - 1)) : Gap;
            foreach (var child in line)
            {
                child.Arrange(new Rect(x, y + (AlignTop ? 0 : AlignBottom ? height - child.DesiredSize.Height : (height - child.DesiredSize.Height) / 2), Math.Min(final.Width, child.DesiredSize.Width), child.DesiredSize.Height));
                x += child.DesiredSize.Width + gap;
            }
            y += height + RowGap;
        }
        return final;
    }
}

internal sealed class DnsPagingActions : DnsFlowPanel
{
    internal bool Narrow { get; set; }
    protected override Size MeasureOverride(Size available)
    {
        if (!Narrow) return base.MeasureOverride(available);
        double width = Math.Max(0, (available.Width - 8) / 2);
        foreach (UIElement child in InternalChildren) child.Measure(new Size(ReferenceEquals(child, InternalChildren[^1]) ? available.Width : width, double.PositiveInfinity));
        return new Size(available.Width, 28 * 3 + 8 * 2);
    }
    protected override Size ArrangeOverride(Size final)
    {
        if (!Narrow) return base.ArrangeOverride(final);
        double width = Math.Max(0, (final.Width - 8) / 2);
        for (int index = 0; index < InternalChildren.Count; index++)
        {
            var child = InternalChildren[index];
            if (index < 4) child.Arrange(new Rect(index % 2 * (width + 8), index / 2 * 36, width, 28));
            else child.Arrange(new Rect(0, 72, final.Width, 28));
        }
        return final;
    }
}

internal sealed class DnsFilterPanel : Panel
{
    private readonly List<(UIElement Item, Rect Rect)> positions = [];
    protected override Size MeasureOverride(Size available)
    {
        positions.Clear(); var items = InternalChildren.Cast<UIElement>().ToArray();
        double y = 0; int first = 0;
        while (first < items.Length)
        {
            double used = 0; int end = first;
            while (end < items.Length)
            {
                double basis = end == 0 ? 220 : end == 2 ? 120 : end < 4 ? 150 : Natural(items[end]);
                if (end > first && used + 12 + basis > available.Width) break;
                used += (end == first ? 0 : 12) + Math.Min(basis, available.Width); end++;
            }
            int grow = Enumerable.Range(first, end - first).Count(index => index < 4 && index != 2);
            double extra = grow > 0 ? Math.Max(0, available.Width - used) / grow : 0, x = 0, height = 0;
            for (int index = first; index < end; index++)
            {
                double basis = index == 0 ? 220 : index == 2 ? 120 : index < 4 ? 150 : Natural(items[index]);
                double width = Math.Min(available.Width, basis + (index < 4 && index != 2 ? extra : 0));
                items[index].Measure(new Size(width, double.PositiveInfinity)); height = Math.Max(height, items[index].DesiredSize.Height);
                positions.Add((items[index], new Rect(x, y, width, items[index].DesiredSize.Height))); x += width + 12;
            }
            for (int index = positions.Count - (end - first); index < positions.Count; index++)
            { var entry = positions[index]; entry.Rect.Y += height - entry.Rect.Height; positions[index] = entry; }
            y += height + 12; first = end;
        }
        return new Size(available.Width, Math.Max(0, y - 12));
        static double Natural(UIElement child) { child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return child.DesiredSize.Width; }
    }
    protected override Size ArrangeOverride(Size final) { foreach (var item in positions) item.Item.Arrange(item.Rect); return final; }
}

internal sealed class DnsTabs : TabControl
{
    private Canvas? rail;
    private Border? marker;
    private ContentPresenter? body;
    private bool placed;
    internal DnsTabs()
    {
        Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="TabControl">
              <StackPanel>
                <Grid Margin="0,0,0,20" ClipToBounds="True">
                  <Border BorderThickness="0,0,0,1" BorderBrush="{DynamicResource DividerBrush}"/>
                  <TabPanel IsItemsHost="True" Margin="0,0,0,1"/>
                  <Canvas Name="PART_Rail" IsHitTestVisible="False" VerticalAlignment="Bottom" Height="2"><Border Name="PART_Marker" Height="2" CornerRadius="2,2,0,0" Background="{DynamicResource AccentBrush}"/></Canvas>
                </Grid>
                <ContentPresenter Name="PART_SelectedContentHost" ContentSource="SelectedContent"/>
              </StackPanel>
            </ControlTemplate>
            """);
        ItemContainerStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TabItem">
              <Setter Property="Padding" Value="14,10,14,11"/><Setter Property="Margin" Value="0,0,4,0"/><Setter Property="FontSize" Value="14"/><Setter Property="FontWeight" Value="Medium"/>
              <Setter Property="Foreground" Value="{DynamicResource TextTertiaryBrush}"/><Setter Property="Background" Value="Transparent"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem"><Border Background="Transparent" Padding="{TemplateBinding Padding}"><TextBlock Text="{TemplateBinding Header}" LineHeight="20.02" TextWrapping="NoWrap"/></Border></ControlTemplate></Setter.Value></Setter>
              <Style.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{DynamicResource AccentBrush}"/><Setter Property="FontWeight" Value="SemiBold"/></Trigger></Style.Triggers>
            </Style>
            """);
        SelectionChanged += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Place(true))); };
        Loaded += (_, _) => { L.Changed += LanguageChanged; Place(true); }; Unloaded += (_, _) => L.Changed -= LanguageChanged; SizeChanged += (_, _) => Place(false);
    }
    public override void OnApplyTemplate() { base.OnApplyTemplate(); rail = GetTemplateChild("PART_Rail") as Canvas; marker = GetTemplateChild("PART_Marker") as Border; body = GetTemplateChild("PART_SelectedContentHost") as ContentPresenter; Place(false); }
    private void LanguageChanged() => _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Place(false)));
    private void Place(bool animate)
    {
        if (rail is null || marker is null || !IsLoaded || SelectedItem is not TabItem item || item.ActualWidth <= 0) return;
        double left = item.TranslatePoint(new Point(10, 0), rail).X, width = Math.Max(0, item.ActualWidth - 20);
        double previousLeft = Canvas.GetLeft(marker), previousWidth = marker.ActualWidth;
        marker.BeginAnimation(Canvas.LeftProperty, null); marker.BeginAnimation(WidthProperty, null); Canvas.SetLeft(marker, left); marker.Width = width;
        if (animate && placed && FluentMotion.Enabled && double.IsFinite(previousLeft))
        {
            marker.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(previousLeft, left, TimeSpan.FromMilliseconds(167)) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
            marker.BeginAnimation(WidthProperty, new DoubleAnimation(previousWidth, width, TimeSpan.FromMilliseconds(167)) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
        }
        if (animate && body is not null && FluentMotion.Enabled)
        {
            var transform = new TranslateTransform(); body.RenderTransform = transform;
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
            body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
        }
        placed = true;
    }
}

internal sealed class DnsMeter : FrameworkElement
{
    private static readonly DependencyProperty FreshProperty = DependencyProperty.Register("Fresh", typeof(double), typeof(DnsMeter), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty StaleProperty = DependencyProperty.Register("Stale", typeof(double), typeof(DnsMeter), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    internal double Duration { get; init; } = 250;
    internal bool TopList { get; init; }
    internal DnsMeter() { Height = 4; ClipToBounds = true; Loaded += (_, _) => DesignTokens.Changed += InvalidateVisual; Unloaded += (_, _) => DesignTokens.Changed -= InvalidateVisual; }
    internal void Set(double fresh, double stale = 0)
    {
        Update(FreshProperty, Math.Clamp(fresh, 0, 1)); Update(StaleProperty, Math.Clamp(stale, 0, 1));
        void Update(DependencyProperty property, double value)
        {
            double from = (double)GetValue(property); BeginAnimation(property, null); SetValue(property, value);
            if (IsLoaded && Duration > 0 && FluentMotion.Enabled && Math.Abs(from - value) > .0001)
                BeginAnimation(property, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(Duration)) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
        }
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context); if (ActualWidth <= 0) return;
        double radius = ActualHeight / 2, fresh = (double)GetValue(FreshProperty), stale = (double)GetValue(StaleProperty);
        context.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius));
        context.DrawRectangle(TryFindResource(TopList ? "SubtleHoverBrush" : "DividerBrush") as Brush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        double filled = Math.Min(ActualWidth, TopList ? Math.Max(2, ActualWidth * fresh) : ActualWidth * fresh);
        context.DrawRoundedRectangle(TryFindResource("AccentBrush") as Brush, null, new Rect(0, 0, filled, ActualHeight), radius, radius);
        if (stale > 0) { context.PushOpacity(.55); context.DrawRectangle(TryFindResource("ChartUpBrush") as Brush, null, new Rect(filled, 0, Math.Min(ActualWidth - filled, ActualWidth * stale), ActualHeight)); context.Pop(); }
        context.Pop();
    }
}

internal sealed class DnsTopList : StackPanel
{
    private readonly Dictionary<string, (Border Row, TextBlock Rank, TextBlock Name, TextBlock Count, DnsMeter Meter)> rows = new(StringComparer.Ordinal);
    internal void Set(IEnumerable<(string Name, double Count)> source)
    {
        var items = source.Take(20).ToArray(); var keys = items.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string key in rows.Keys.Where(key => !keys.Contains(key)).ToArray()) { Children.Remove(rows[key].Row); rows.Remove(key); }
        foreach (var placeholder in Children.OfType<TextBlock>().ToArray()) Children.Remove(placeholder);
        double max = Math.Max(1, items.Select(item => item.Count).DefaultIfEmpty().Max());
        if (items.Length == 0) { var empty = DnsVisuals.Caption("暂时没有 DNS 查询。"); empty.TextAlignment = TextAlignment.Center; empty.Margin = new Thickness(16, 32, 16, 32); Children.Add(empty); return; }
        for (int index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!rows.TryGetValue(item.Name, out var row))
            {
                var grid = new Grid(); foreach (var width in new[] { new GridLength(22), new GridLength(10), new GridLength(1, GridUnitType.Star), new GridLength(10), new GridLength(48), new GridLength(10), new GridLength(52) }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
                grid.ColumnDefinitions[4].MinWidth = 48;
                grid.SizeChanged += (_, _) => grid.ColumnDefinitions[4].Width = new GridLength(Math.Max(48, grid.ActualWidth * .26));
                var rank = Ui.Text("", 11); rank.FontFamily = DnsVisuals.Mono; rank.SetResourceReference(TextBlock.ForegroundProperty, "TextDisabledBrush");
                var name = Ui.Text("", 13); name.LineHeight = 18.59; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
                var count = Ui.Text("", 12); count.FontFamily = DnsVisuals.Mono; count.FontWeight = FontWeights.Medium; count.TextAlignment = TextAlignment.Right;
                var meter = new DnsMeter { Duration = 700, TopList = true, Height = 6, VerticalAlignment = VerticalAlignment.Center };
                grid.Children.Add(rank); grid.Children.Add(name); Grid.SetColumn(name, 2); grid.Children.Add(meter); Grid.SetColumn(meter, 4); grid.Children.Add(count); Grid.SetColumn(count, 6);
                var border = new Border { Child = grid, Padding = new Thickness(0, 8, 0, 8) }; border.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
                border.MouseEnter += (_, _) => border.SetResourceReference(ShellVisuals.BackgroundProperty, "SubtleHoverBrush"); border.MouseLeave += (_, _) => ShellVisuals.SetBackground(border, Brushes.Transparent);
                row = (border, rank, name, count, meter); rows[item.Name] = row;
            }
            row.Row.BorderThickness = new Thickness(0, index == 0 ? 0 : 1, 0, 0); row.Rank.Text = (index + 1).ToString(CultureInfo.CurrentCulture);
            row.Name.Text = item.Name == "unknown" ? L.T("未知") : item.Name; row.Name.ToolTip = item.Name; row.Count.Text = DnsVisuals.Number(item.Count); row.Meter.Set(item.Count / max);
            int oldIndex = Children.IndexOf(row.Row); if (oldIndex != index) { if (oldIndex >= 0) Children.RemoveAt(oldIndex); Children.Insert(index, row.Row); }
        }
    }
}

internal sealed class DnsLoadingBar : FrameworkElement
{
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register("Progress", typeof(double), typeof(DnsLoadingBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    internal DnsLoadingBar()
    {
        Width = 120; Height = 3; HorizontalAlignment = HorizontalAlignment.Left; Margin = new Thickness(0, 0, 0, 16);
        Loaded += (_, _) => Start(); IsVisibleChanged += (_, _) => { BeginAnimation(ProgressProperty, null); if (IsVisible) Start(); }; Unloaded += (_, _) => BeginAnimation(ProgressProperty, null);
    }
    private void Start()
    {
        if (!IsLoaded || !IsVisible || !FluentMotion.Enabled) return;
        BeginAnimation(ProgressProperty, new DoubleAnimation(-.4, 1.04, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new CubicBezierEase(.8, 0, .2, 1), RepeatBehavior = RepeatBehavior.Forever });
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context); context.PushClip(new RectangleGeometry(new Rect(0, 0, 120, 3), 2, 2));
        context.DrawRectangle(TryFindResource("DividerBrush") as Brush, null, new Rect(0, 0, 120, 3));
        context.DrawRoundedRectangle(TryFindResource("AccentBrush") as Brush, null, new Rect((double)GetValue(ProgressProperty) * 120, 0, 48, 3), 2, 2); context.Pop();
    }
}

public sealed class DnsOutcome : Border
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(DnsOutcome), new PropertyMetadata("", (owner, _) => ((DnsOutcome)owner).Update()));
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    private readonly TextBlock label = Ui.Text("", 12);
    private readonly Border dot = new() { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Opacity = .8, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    public DnsOutcome()
    {
        Height = 22; Padding = new Thickness(9, 0, 9, 0); CornerRadius = new CornerRadius(11); HorizontalAlignment = HorizontalAlignment.Left;
        label.FontWeight = FontWeights.Medium; Child = DnsVisuals.Inline(dot, label);
        Loaded += (_, _) => { L.Changed += Update; DesignTokens.Changed += Update; Update(); }; Unloaded += (_, _) => { L.Changed -= Update; DesignTokens.Changed -= Update; };
    }
    private void Update()
    {
        string text = Value switch { "cache_fresh" => "有效缓存", "cache_stale" => "过期缓存", "hosts" => "Hosts", "fake_ip" => "Fake IP", "upstream" => "上游", "reject" => "拒绝", "drop" => "丢弃", "error" => "错误", "local" => "本地答复", _ => Value };
        label.Text = L.T(text); string foreground = Value is "cache_fresh" or "cache_stale" ? "SuccessBrush" : Value is "error" or "reject" or "drop" ? "DangerBrush" : Value == "upstream" ? "AccentBrush" : "TextSecondaryBrush";
        string background = Value is "cache_fresh" or "cache_stale" ? "SuccessBackgroundBrush" : Value is "error" or "reject" or "drop" ? "CriticalBackgroundBrush" : "SubtleHoverBrush";
        label.SetResourceReference(TextBlock.ForegroundProperty, foreground); dot.SetResourceReference(Border.BorderBrushProperty, foreground);
        if (Value == "cache_stale") { dot.Background = Brushes.Transparent; dot.BorderThickness = new Thickness(1.5); }
        else { dot.BorderThickness = new Thickness(0); dot.SetResourceReference(Border.BackgroundProperty, foreground); }
        if (Value == "upstream" && TryFindResource("AccentBrush") is Brush accent) { var brush = accent.CloneCurrentValue(); brush.Opacity *= .12; Background = brush; }
        else SetResourceReference(Border.BackgroundProperty, background);
    }
}
