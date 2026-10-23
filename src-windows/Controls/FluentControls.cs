using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>The small set of native control templates used by the uploaded Fluent shell.</summary>
internal static class FluentControls
{
    private static readonly ResourceDictionary templates = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="Button" TargetType="Button">
            <Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource BorderBrush}"/><Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="12,6"/><Setter Property="MinHeight" Value="32"/>
            <Setter Property="HorizontalContentAlignment" Value="Center"/><Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.84"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.65"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Ghost" TargetType="Button" BasedOn="{StaticResource Button}">
            <Setter Property="Background" Value="Transparent"/><Setter Property="BorderBrush" Value="Transparent"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource SubtleSurfaceBrush}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.65"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="HomeTile" TargetType="ToggleButton">
            <Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
            <Setter Property="Padding" Value="16,12,8,12"/><Setter Property="MinHeight" Value="64"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
              <Border x:Name="Frame" Background="{TemplateBinding Background}" CornerRadius="7,0,0,7" Padding="{TemplateBinding Padding}" BorderBrush="Transparent" BorderThickness="1">
                <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Stretch"/></Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="#14000000"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.65"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource TextPrimaryBrush}"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Mode" TargetType="RadioButton">
            <Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="{DynamicResource TextSecondaryBrush}"/>
            <Setter Property="Padding" Value="14,5"/><Setter Property="MinHeight" Value="30"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
              <Grid><Border x:Name="Frame" Background="{TemplateBinding Background}" CornerRadius="4" BorderThickness="1" BorderBrush="Transparent" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                <Border x:Name="Mark" Height="2" Width="16" CornerRadius="1" Background="{DynamicResource AccentBrush}" HorizontalAlignment="Center" VerticalAlignment="Bottom" Visibility="Collapsed" Margin="0,0,0,2"/>
              </Grid><ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource SubtleSurfaceBrush}"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource SurfaceBrush}"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/><Setter TargetName="Mark" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Switch" TargetType="ToggleButton">
            <Setter Property="Width" Value="40"/><Setter Property="Height" Value="24"/><Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
              <Grid Background="Transparent"><Border x:Name="Track" Height="20" CornerRadius="10" BorderThickness="1" BorderBrush="{DynamicResource TextSecondaryBrush}" Background="Transparent"/>
                <Ellipse x:Name="Thumb" Width="12" Height="12" Margin="4,0,0,0" HorizontalAlignment="Left" Fill="{DynamicResource TextSecondaryBrush}"/>
                <Border x:Name="Focus" Margin="-3" BorderBrush="{DynamicResource AccentBrush}" BorderThickness="2" CornerRadius="12" Visibility="Collapsed"/>
              </Grid><ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Track" Property="Background" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Track" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Thumb" Property="Fill" Value="{DynamicResource AccentTextBrush}"/><Setter TargetName="Thumb" Property="HorizontalAlignment" Value="Right"/><Setter TargetName="Thumb" Property="Margin" Value="0,0,4,0"/></Trigger>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Thumb" Property="Width" Value="14"/><Setter TargetName="Thumb" Property="Height" Value="14"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Visibility" Value="Visible"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="QuickToggle" TargetType="ToggleButton">
            <Setter Property="Height" Value="48"/><Setter Property="Background" Value="{DynamicResource SurfaceBrush}"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
              <Border x:Name="Frame" CornerRadius="4" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" Background="{TemplateBinding Background}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource AccentBrush}"/><Setter Property="Foreground" Value="{DynamicResource AccentTextBrush}"/></Trigger>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.85"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.65"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource AccentBrush}"/><Setter TargetName="Frame" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
    internal static Style Style(string key) => (Style)templates[key];
    internal static Button IconButton(string icon, string label, string id, double size = 32)
    {
        var button = new Button { Content = new FluentIcon(icon, 16), Width = size, Height = size, Padding = new Thickness(4), Style = Style("Ghost") }.Id(id);
        L.Bind(button, FrameworkElement.ToolTipProperty, label); L.Bind(button, AutomationProperties.NameProperty, label);
        return button;
    }
    internal static void Primary(Button button)
    {
        button.Style = Style("Button");
        button.SetResourceReference(Control.BackgroundProperty, "AccentBrush"); button.SetResourceReference(Control.ForegroundProperty, "AccentTextBrush");
    }
    internal static FrameworkElement ButtonContent(string icon, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new FluentIcon(icon, 16) { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Ui.Label(label)); return row;
    }
}

public sealed class ModeSelector : UserControl
{
    private readonly RadioButton[] options;
    private readonly string[] values = ["rule", "global", "direct"];
    private bool applying;
    public event Action<string>? Selected;
    public string Mode { get; private set; } = "rule";
    public ModeSelector(string id = "OutboundMode")
    {
        this.Id(id); L.Bind(this, AutomationProperties.NameProperty, "出站模式");
        var panel = new UniformGrid { Columns = 3 };
        string group = Guid.NewGuid().ToString("N");
        options = values.Select((value, index) =>
        {
            var option = new RadioButton { Content = Ui.Text(L.ShortMode(value)), GroupName = group, Style = FluentControls.Style("Mode") }.Id(id + "_" + value);
            AutomationProperties.SetName(option, L.ShortMode(value));
            option.Checked += (_, _) => { if (!applying && option.IsEnabled) { Mode = value; Selected?.Invoke(value); } };
            panel.Children.Add(option); return option;
        }).ToArray();
        var border = new Border { Padding = new Thickness(2), CornerRadius = new CornerRadius(6), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush"); Content = border;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
            int current = Array.FindIndex(options, item => item.IsChecked == true);
            int next = e.Key switch { Key.Home => 0, Key.End => 2, Key.Left => (current + 2) % 3, _ => (current + 1) % 3 };
            options[next].IsChecked = true; options[next].Focus(); e.Handled = true;
        };
        Loaded += (_, _) => { L.Changed += UpdateLabels; UpdateLabels(); };
        Unloaded += (_, _) => L.Changed -= UpdateLabels;
        SetMode("rule");
    }
    private void UpdateLabels()
    {
        for (int index = 0; index < options.Length; index++)
        {
            ((TextBlock)options[index].Content).Text = L.ShortMode(values[index]);
            AutomationProperties.SetName(options[index], L.ShortMode(values[index]));
        }
    }
    public void SetMode(string mode)
    {
        applying = true;
        try { Mode = values.Contains(mode) ? mode : "rule"; for (int index = 0; index < options.Length; index++) options[index].IsChecked = values[index] == Mode; }
        finally { applying = false; }
    }
}

internal sealed class QuickSettingTile : Border
{
    private readonly ToggleButton toggle;
    private readonly TextBlock description = Ui.Muted("");
    private readonly FluentProgressRing progress = new() { Width = 20, Height = 20 };
    private readonly FluentIcon glyph;
    private readonly TextBlock label;
    private readonly Button? moreButton;
    private readonly string title;
    private bool applying;
    public event Action<bool>? Changed;
    public QuickSettingTile(string icon, string title, string id, Action? more = null)
    {
        this.title = title;
        CornerRadius = new CornerRadius(8); BorderThickness = new Thickness(1); MinHeight = 64;
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(BorderBrushProperty, "BorderBrush");
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition());
        glyph = new FluentIcon(icon, 20) { VerticalAlignment = VerticalAlignment.Center };
        var iconSlot = new Grid { Width = 20, Height = 24, Margin = new Thickness(0, 0, 14, 0) }; iconSlot.Children.Add(glyph); iconSlot.Children.Add(progress); row.Children.Add(iconSlot);
        label = Ui.Label(title, 14, true); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis;
        description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis;
        var texts = Ui.Column(label, description); texts.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(texts, 1); row.Children.Add(texts);
        toggle = new ToggleButton { Style = FluentControls.Style("HomeTile"), Content = row }.Id(id);
        L.Bind(toggle, AutomationProperties.NameProperty, title);
        toggle.Checked += ChangedState; toggle.Unchecked += ChangedState;
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition()); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); layout.Children.Add(toggle);
        if (more is not null)
        {
            moreButton = FluentControls.IconButton("chevron-right", title, id + "Settings", 36); moreButton.Style = FluentControls.Style("Button"); moreButton.Background = Brushes.Transparent; moreButton.Height = double.NaN; moreButton.VerticalAlignment = VerticalAlignment.Stretch;
            moreButton.BorderThickness = new Thickness(1, 0, 0, 0); moreButton.SetResourceReference(Control.BorderBrushProperty, "BorderBrush"); moreButton.Click += (_, _) => more();
            Grid.SetColumn(moreButton, 1); layout.Children.Add(moreButton);
        }
        Child = layout;
    }
    private void ChangedState(object sender, RoutedEventArgs e) { if (!applying && toggle.IsEnabled) Changed?.Invoke(toggle.IsChecked == true); }
    public void SetState(bool enabled, string subtitle, bool interactive, bool busy = false)
    {
        applying = true;
        try
        {
            toggle.IsChecked = enabled; toggle.IsEnabled = interactive; description.Text = subtitle;
            glyph.Visibility = busy ? Visibility.Hidden : Visibility.Visible; progress.IsActive = busy; glyph.Filled = enabled;
            progress.SetResourceReference(FluentProgressRing.ForegroundProperty, enabled ? "AccentTextBrush" : "AccentBrush");
            SetResourceReference(BackgroundProperty, enabled ? "AccentBrush" : "SurfaceBrush");
            SetResourceReference(BorderBrushProperty, enabled ? "AccentBrush" : "BorderBrush");
            toggle.SetResourceReference(Control.ForegroundProperty, enabled ? "AccentTextBrush" : "TextPrimaryBrush");
            description.SetResourceReference(TextBlock.ForegroundProperty, enabled ? "AccentTextBrush" : "TextSecondaryBrush"); description.Opacity = enabled ? .85 : 1;
            if (moreButton is not null)
            {
                moreButton.SetResourceReference(Control.ForegroundProperty, enabled ? "AccentTextBrush" : "TextPrimaryBrush");
                string name = L.F("{0}设置", L.T(title)); moreButton.ToolTip = name; AutomationProperties.SetName(moreButton, name);
            }
            AutomationProperties.SetHelpText(toggle, subtitle);
        }
        finally { applying = false; }
    }
}
