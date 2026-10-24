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
    internal static Style Style(string key) => (Style)Application.Current.FindResource(key switch
    {
        "Button" => "FluentButton", "Primary" => "FluentPrimary", "Ghost" => "FluentGhost", "Switch" => "FluentSwitch",
        "HomeTile" => "FluentHomeTile", "QuickToggle" => "FluentQuickToggle", _ => key
    });
    internal static Button IconButton(string icon, string label, string id, double size = 32)
    {
        var button = new Button { Content = new FluentIcon(icon, 16), Width = size, Height = size, Padding = new Thickness(4), Style = Style("Ghost") }.Id(id);
        L.Bind(button, FrameworkElement.ToolTipProperty, label); L.Bind(button, AutomationProperties.NameProperty, label);
        return button;
    }
    internal static void Primary(Button button)
    {
        button.Style = Style("Primary");
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
    private readonly SettingsSegments segments = new();
    private readonly ListBoxItem[] options;
    private readonly string[] values = ["rule", "global", "direct"];
    private bool applying;
    public event Action<string>? Selected;
    public string Mode { get; private set; } = "rule";
    public bool Stretch { get => segments.Stretch; set => segments.Stretch = value; }
    public ModeSelector(string id = "OutboundMode")
    {
        this.Id(id); L.Bind(this, AutomationProperties.NameProperty, "出站模式");
        HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Center;
        segments.HorizontalAlignment = HorizontalAlignment.Stretch;
        options = values.Select(value =>
        {
            var option = new ListBoxItem { Content = Ui.Text(L.ShortMode(value)), Tag = value }.Id(id + "_" + value);
            AutomationProperties.SetName(option, L.ShortMode(value)); segments.Items.Add(option); return option;
        }).ToArray();
        segments.SelectionChanged += (_, _) =>
        {
            if (!applying && segments.SelectedItem is ListBoxItem { Tag: string value }) { Mode = value; Selected?.Invoke(value); }
        };
        Content = segments;
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
        try { Mode = values.Contains(mode) ? mode : "rule"; segments.SelectedIndex = Array.IndexOf(values, Mode); }
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
        description.LineHeight = 16; description.TextWrapping = TextWrapping.NoWrap; description.TextTrimming = TextTrimming.CharacterEllipsis;
        var texts = Ui.Column(label, description); texts.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(texts, 1); row.Children.Add(texts);
        toggle = new ToggleButton { Style = FluentControls.Style("HomeTile"), Content = row }.Id(id);
        L.Bind(toggle, AutomationProperties.NameProperty, title);
        toggle.Checked += ChangedState; toggle.Unchecked += ChangedState;
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition()); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); layout.Children.Add(toggle);
        if (more is not null)
        {
            moreButton = FluentControls.IconButton("chevron-right", title, id + "Settings", 36); moreButton.Content = new FluentIcon("chevron-right", 12); moreButton.Style = FluentControls.Style("FluentHomeMore"); moreButton.Height = double.NaN; moreButton.VerticalAlignment = VerticalAlignment.Stretch;
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
            description.SetResourceReference(TextBlock.ForegroundProperty, enabled ? "AccentTextSecondaryBrush" : "TextSecondaryBrush"); description.Opacity = 1;
            if (moreButton is not null)
            {
                moreButton.Tag = enabled;
                moreButton.SetResourceReference(Control.ForegroundProperty, enabled ? "AccentTextBrush" : "TextPrimaryBrush");
                moreButton.SetResourceReference(Control.BorderBrushProperty, enabled ? "OnAccentDividerBrush" : "DividerBrush");
                string name = L.F("{0}设置", L.T(title)); moreButton.ToolTip = name; AutomationProperties.SetName(moreButton, name);
            }
            AutomationProperties.SetHelpText(toggle, subtitle);
        }
        finally { applying = false; }
    }
}
