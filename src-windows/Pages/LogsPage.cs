using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Mihomo.Desktop.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Pages;

public sealed class LogsPage : NativePage
{
    private static readonly ResourceDictionary templates = (ResourceDictionary)XamlReader.Parse("""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style x:Key="Line" TargetType="ListBoxItem">
            <Setter Property="Padding" Value="0"/><Setter Property="Margin" Value="0"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem"><ContentPresenter HorizontalAlignment="Stretch"/></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Pause" TargetType="ToggleButton">
            <Setter Property="Height" Value="32"/><Setter Property="Padding" Value="11,0"/><Setter Property="FontSize" Value="14"/>
            <Setter Property="FontFamily" Value="{DynamicResource BodyFont}"/><Setter Property="Foreground" Value="{DynamicResource TextPrimaryBrush}"/>
            <Setter Property="Background" Value="{DynamicResource ControlBrush}"/><Setter Property="BorderBrush" Value="{DynamicResource ControlStrokeBrush}"/>
            <Setter Property="FocusVisualStyle" Value="{DynamicResource FluentFocus}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
              <Border x:Name="Frame" CornerRadius="4" BorderThickness="1" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" Padding="{TemplateBinding Padding}">
                <Grid><Border Height="1" Background="{DynamicResource ControlElevationBrush}" VerticalAlignment="Bottom" Margin="-11,0,-11,0"/><ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/></Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource ControlHoverBrush}"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource ControlPressedBrush}"/></Trigger>
                <Trigger Property="IsChecked" Value="True"><Setter TargetName="Frame" Property="Background" Value="{DynamicResource PanelSolidBrush}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{DynamicResource WarningBrush}"/><Setter Property="Foreground" Value="{DynamicResource WarningBrush}"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{DynamicResource DisabledTextBrush}"/><Setter TargetName="Frame" Property="Background" Value="{DynamicResource ControlDisabledBrush}"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style x:Key="Resume" TargetType="Button">
            <Setter Property="Height" Value="28"/><Setter Property="Padding" Value="12,0"/><Setter Property="FontSize" Value="12"/><Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="FontFamily" Value="{DynamicResource BodyFont}"/><Setter Property="Foreground" Value="{DynamicResource AccentTextBrush}"/>
            <Setter Property="Background" Value="{DynamicResource AccentBrush}"/><Setter Property="FocusVisualStyle" Value="{DynamicResource FluentFocus}"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
              <Border x:Name="Frame" CornerRadius="999" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"><ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/></Border>
            </ControlTemplate></Setter.Value></Setter>
          </Style>
        </ResourceDictionary>
        """);
    private const int Capacity = 2000;
    private readonly ObservableCollection<LogRow> lines = [];
    private readonly List<LogRow> history = [];
    private readonly Queue<LogRow> held = new();
    private readonly ListBox table = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0, 8, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox level = new() { MinWidth = 112, Height = 32, SelectedValuePath = "Tag" };
    private readonly ToggleButton pause = new() { Style = (Style)templates["Pause"], Margin = new Thickness(0, 0, 8, 0) };
    private readonly MenuItem follow = new() { IsCheckable = true, IsChecked = true };
    private readonly TextBlock count = Ui.Muted("", 14);
    private readonly Grid empty = new() { IsHitTestVisible = false };
    private readonly TextBlock pausedText = Ui.Hint("已暂停");
    private readonly TextBlock heldCount = Ui.Text("", 12);
    private readonly StackPanel heldLabel;
    private readonly Border pausedNotice;
    private readonly Border pausedLamp;
    private readonly Border pausedGlow;
    private readonly Border card;
    private readonly SolidColorBrush cardStroke = new();
    private readonly SolidColorBrush resumeFill = new();
    private readonly TranslateTransform noticeShift = new();
    private readonly ScaleTransform noticeScale = new(1, 1);
    private readonly DispatcherTimer noticeExit = new() { Interval = TimeSpan.FromMilliseconds(167) };
    private readonly Button clear;
    private readonly Button resume;
    private ScrollViewer? listScroll;
    private CancellationTokenSource? stream;
    private string controller = "";
    private string defaultLevel = "info";
    private string streamLevel = "";
    private string streamController = "";
    private bool clearing;
    private bool followLatest = true;
    private bool updatingFollow;
    private bool followScrollPending;
    private bool noticeShown;
    private bool? pauseLabelState;
    private int clearGeneration;
    private int previousHeld = -1;

    public LogsPage(AppSession session) : base(session, "logs", "日志", "")
    {
        level.Id("LogLevel"); pause.Id("PauseLogs"); follow.Id("FollowLogs"); table.Id("LogTable"); count.Id("LogCount");
        L.Bind(level, AutomationProperties.NameProperty, "日志等级");
        L.Bind(follow, HeaderedItemsControl.HeaderProperty, "跟随最新");
        foreach (var (name, value) in new[] { ("Default", ""), ("Debug", "debug"), ("Info", "info"), ("Warn", "warning"), ("Error", "error"), ("Silent", "silent") })
            level.Items.Add(new ComboBoxItem { Tag = value, Content = name });
        level.SelectedIndex = 0;
        clear = Action("清空", "ClearLogs", ClearAsync); clear.Margin = new Thickness(0); clear.Content = FluentControls.ButtonContent("trash", "清空");
        var levelLabel = Ui.Hint("日志等级"); levelLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); levelLabel.Margin = new Thickness(0, 0, 8, 0);
        level.Margin = new Thickness(0, 0, 8, 0);
        Tools.Children.Add(levelLabel); Tools.Children.Add(level); Tools.Children.Add(pause); Tools.Children.Add(clear); SetHeaderMeta(count);
        level.SelectionChanged += (_, _) => { if (IsActive) RestartStream(); };
        pause.Checked += (_, _) => PauseChanged(); pause.Unchecked += (_, _) => PauseChanged();
        follow.Checked += (_, _) => { if (!updatingFollow) { followLatest = true; ScrollToLatest(); } };
        follow.Unchecked += (_, _) => { if (!updatingFollow) followLatest = false; };
        var menu = new ContextMenu(); menu.Items.Add(follow); table.ContextMenu = menu;
        table.ItemContainerStyle = (Style)templates["Line"];
        table.ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(LogLineView)) };
        table.ItemsSource = lines;
        VirtualizingPanel.SetIsVirtualizing(table, true); VirtualizingPanel.SetVirtualizationMode(table, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(table, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(table, true); ScrollViewer.SetHorizontalScrollBarVisibility(table, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(table, ScrollBarVisibility.Auto);
        table.MouseDoubleClick += (_, _) => { if (table.SelectedItem is LogRow row) DetailsDialog.Show(this, "日志详情", $"{row.Time}  {row.Level}\n\n{row.Message}"); };
        table.Loaded += (_, _) =>
        {
            if (listScroll is not null) listScroll.ScrollChanged -= OnScrollChanged;
            listScroll = FindVisual<ScrollViewer>(table);
            if (listScroll is not null) listScroll.ScrollChanged += OnScrollChanged;
        };
        var emptyContent = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var dot = new LivePing { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        emptyContent.Children.Add(dot);
        var emptyText = Ui.Hint("等待新的日志…", 13); emptyText.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush"); emptyContent.Children.Add(emptyText); empty.Children.Add(emptyContent);
        resume = new Button { Style = (Style)templates["Resume"], Background = resumeFill }.Id("ResumeLogs");
        L.Bind(resume, ContentControl.ContentProperty, "继续"); L.Bind(resume, AutomationProperties.NameProperty, "继续"); resume.Click += (_, _) => pause.IsChecked = false;
        resume.MouseEnter += (_, _) => UpdateResumeFill(true);
        resume.MouseLeave += (_, _) => UpdateResumeFill(true);
        resume.RenderTransformOrigin = new Point(.5, .5); var press = new ScaleTransform(1, 1); resume.RenderTransform = press;
        resume.PreviewMouseLeftButtonDown += (_, _) => PressResume(.96);
        resume.PreviewMouseLeftButtonUp += (_, _) => PressResume(1); resume.MouseLeave += (_, _) => PressResume(1);
        void PressResume(double value)
        {
            double old = press.ScaleX; press.ScaleX = press.ScaleY = value;
            if (FluentMotion.Enabled) { SettingsSegments.Animate(press, ScaleTransform.ScaleXProperty, old, value, 83); SettingsSegments.Animate(press, ScaleTransform.ScaleYProperty, old, value, 83); }
        }
        pausedLamp = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        pausedLamp.SetResourceReference(Border.BackgroundProperty, "WarningBrush");
        pausedText.FontWeight = FontWeights.SemiBold; pausedText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush"); pausedText.Margin = new Thickness(0, 0, 12, 0);
        heldCount.FontFamily = new FontFamily("Cascadia Mono, Consolas"); heldCount.FontWeight = FontWeights.Medium; heldCount.Margin = new Thickness(0, 0, 4, 0);
        heldLabel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        heldLabel.Children.Add(heldCount); heldLabel.Children.Add(Ui.Hint("条新日志等待显示"));
        var noticeLayout = new Grid();
        noticeLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); noticeLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noticeLayout.ColumnDefinitions.Add(new ColumnDefinition()); noticeLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        noticeLayout.Children.Add(pausedLamp); Grid.SetColumn(pausedText, 1); noticeLayout.Children.Add(pausedText);
        Grid.SetColumn(heldLabel, 2); noticeLayout.Children.Add(heldLabel); Grid.SetColumn(resume, 3); noticeLayout.Children.Add(resume);
        pausedNotice = new Border { Child = noticeLayout, Padding = new Thickness(14, 6, 6, 6), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(999), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 14), Visibility = Visibility.Collapsed, RenderTransformOrigin = new Point(.5, .5) };
        var transform = new TransformGroup(); transform.Children.Add(noticeScale); transform.Children.Add(noticeShift); pausedNotice.RenderTransform = transform;
        AutomationProperties.SetLiveSetting(pausedNotice, AutomationLiveSetting.Polite);
        noticeExit.Tick += (_, _) => { noticeExit.Stop(); if (pause.IsChecked != true) { pausedNotice.Visibility = Visibility.Collapsed; pausedLamp.BeginAnimation(OpacityProperty, null); } };
        var surface = new Grid(); surface.Children.Add(table); surface.Children.Add(empty); surface.Children.Add(pausedNotice);
        surface.SizeChanged += (_, _) => { surface.Clip = new RectangleGeometry(new Rect(surface.RenderSize), 7, 7); pausedNotice.MaxWidth = Math.Max(1, surface.ActualWidth - ((Window.GetWindow(this)?.ActualWidth ?? 1180) <= 768 ? 20 : 32)); };
        card = new Border { Child = surface, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = cardStroke, MinHeight = 320 };
        card.SetResourceReference(Border.BackgroundProperty, "PanelSolidBrush");
        pausedGlow = new Border { Margin = new Thickness(18), CornerRadius = new CornerRadius(8), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        pausedGlow.SetResourceReference(Border.BackgroundProperty, "WarningBrush");
        var body = new Grid(); body.Children.Add(pausedGlow); body.Children.Add(card); SetBody(body);
        SizeChanged += (_, _) => AdaptNotice(); ApplyTheme(); UpdateCount();
    }

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); if (child is T match) return match; if (FindVisual<T>(child) is { } nested) return nested; }
        return null;
    }
    private void AdaptNotice()
    {
        bool narrow = (Window.GetWindow(this)?.ActualWidth ?? ActualWidth) <= 768;
        pausedNotice.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        pausedNotice.Margin = narrow ? new Thickness(10, 0, 10, 10) : new Thickness(16, 0, 16, 14);
    }
    private Color Color(string key) => TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Transparent;
    private Brush Tint(string key, double opacity) { var color = Color(key); color.A = (byte)Math.Round(color.A * opacity); return new SolidColorBrush(color); }
    private void ApplyTheme()
    {
        pausedNotice.BorderBrush = Tint("WarningBrush", .4); pausedNotice.Background = Tint("PanelSolidBrush", .92);
        pausedNotice.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 8, Direction = 270, Opacity = Color("AppBackgroundBrush").R < 128 ? .26 : .14 };
        pausedLamp.Effect = new DropShadowEffect { Color = Color("WarningBrush"), BlurRadius = 8, ShadowDepth = 0, Opacity = 1 };
        pausedGlow.Effect = new BlurEffect { Radius = 40 };
        UpdateResumeFill(false); UpdateCardEdge(false);
        for (int index = 0; index < table.Items.Count; index++)
            if (table.ItemContainerGenerator.ContainerFromIndex(index) is DependencyObject container) FindVisual<LogLineView>(container)?.RefreshColors();
    }
    private void UpdateCardEdge(bool animate)
    {
        var target = Color(pause.IsChecked == true ? "WarningBrush" : "CardStrokeBrush");
        if (pause.IsChecked == true) target.A = (byte)Math.Round(target.A * .45);
        var current = cardStroke.Color; cardStroke.BeginAnimation(SolidColorBrush.ColorProperty, null); cardStroke.Color = target;
        if (animate && FluentMotion.Enabled) cardStroke.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(current, target, TimeSpan.FromMilliseconds(250)) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
        double opacity = pause.IsChecked == true ? .6 : 0;
        double previousOpacity = pausedGlow.Opacity;
        pausedGlow.BeginAnimation(OpacityProperty, null); pausedGlow.Opacity = opacity; pausedGlow.Visibility = Visibility.Visible;
        if (animate && FluentMotion.Enabled) SettingsSegments.Animate(pausedGlow, OpacityProperty, previousOpacity, opacity, 250);
    }
    private void UpdateResumeFill(bool animate)
    {
        var target = Color("AccentBrush");
        if (resume.IsMouseOver) target = System.Windows.Media.Color.FromArgb(target.A, (byte)Math.Min(255, Math.Round(target.R * 1.08)), (byte)Math.Min(255, Math.Round(target.G * 1.08)), (byte)Math.Min(255, Math.Round(target.B * 1.08)));
        var previous = resumeFill.Color; resumeFill.BeginAnimation(SolidColorBrush.ColorProperty, null); resumeFill.Color = target;
        if (animate && FluentMotion.Enabled) resumeFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(previous, target, TimeSpan.FromMilliseconds(167)) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
    }
    private void PauseChanged()
    {
        if (pause.IsChecked != true && held.Count > 0)
        {
            followLatest = false;
            while (held.TryDequeue(out var item)) Add(item);
            SetFollow(true); ScrollToLatest();
        }
        UpdateCardEdge(true); UpdateCount();
    }
    private void UpdateNotice()
    {
        bool show = pause.IsChecked == true;
        if (show == noticeShown) return;
        noticeShown = show; noticeExit.Stop(); pausedNotice.Visibility = Visibility.Visible;
        double oldOpacity = pausedNotice.Opacity, oldY = noticeShift.Y, oldScale = noticeScale.ScaleX;
        if (show && oldOpacity == 1) { oldOpacity = 0; oldY = 12; oldScale = .98; }
        pausedNotice.Opacity = show ? 1 : 0; noticeShift.Y = show ? 0 : 12; noticeScale.ScaleX = noticeScale.ScaleY = show ? 1 : .98;
        if (FluentMotion.Enabled)
        {
            SettingsSegments.Animate(pausedNotice, OpacityProperty, oldOpacity, show ? 1 : 0, 167);
            SettingsSegments.Animate(noticeShift, TranslateTransform.YProperty, oldY, show ? 0 : 12, 167);
            SettingsSegments.Animate(noticeScale, ScaleTransform.ScaleXProperty, oldScale, show ? 1 : .98, 167);
            SettingsSegments.Animate(noticeScale, ScaleTransform.ScaleYProperty, oldScale, show ? 1 : .98, 167);
            if (show)
            {
                var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1400), RepeatBehavior = RepeatBehavior.Forever };
                blink.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0)));
                blink.KeyFrames.Add(new SplineDoubleKeyFrame(.3, KeyTime.FromPercent(.5), new KeySpline(.42, 0, .58, 1)));
                blink.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromPercent(1), new KeySpline(.42, 0, .58, 1))); pausedLamp.BeginAnimation(OpacityProperty, blink);
            }
            else noticeExit.Start();
        }
        else if (!show) pausedNotice.Visibility = Visibility.Collapsed;
    }
    private async Task ClearAsync()
    {
        if (clearing) return;
        history.Clear(); held.Clear();
        int generation = ++clearGeneration;
        if (!FluentMotion.Enabled) { lines.Clear(); UpdateCount(); return; }
        clearing = true; UpdateCount();
        var shift = new TranslateTransform(0, -12); var blur = new BlurEffect { Radius = 4 }; table.RenderTransform = shift; table.Effect = blur; table.Opacity = 0;
        SettingsSegments.Animate(table, OpacityProperty, 1, 0, 220); SettingsSegments.Animate(shift, TranslateTransform.YProperty, 0, -12, 220); SettingsSegments.Animate(blur, BlurEffect.RadiusProperty, 0, 4, 220);
        await Task.Delay(220);
        if (generation != clearGeneration) return;
        FinishClear();
    }
    private void FinishClear()
    {
        ++clearGeneration; clearing = false;
        table.BeginAnimation(OpacityProperty, null); table.Opacity = 1; table.RenderTransform = Transform.Identity; table.Effect = null;
        if (history.Count > 0) history[^1].ArrivedAt = Stopwatch.GetTimestamp();
        lines.Clear(); foreach (var item in history) lines.Add(item); UpdateCount();
        if (followLatest) ScrollToLatest();
    }
    private void SetFollow(bool enabled)
    {
        followLatest = enabled; updatingFollow = true;
        try { follow.IsChecked = enabled; } finally { updatingFollow = false; }
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (!followScrollPending && args.VerticalChange != 0 && args.ExtentHeightChange == 0 && listScroll is not null)
            SetFollow(listScroll.VerticalOffset + listScroll.ViewportHeight >= listScroll.ExtentHeight - 1);
    }
    private void ScrollToLatest()
    {
        if (clearing || lines.Count == 0 || followScrollPending) return;
        followScrollPending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            try
            {
                if (!IsActive || !followLatest || clearing || lines.Count == 0) return;
                table.ScrollIntoView(lines[^1]);
                listScroll?.ScrollToEnd();
            }
            finally { followScrollPending = false; }
        }));
    }
    public override void Activate()
    {
        if (IsActive) return;
        Session.ThemeChanged += ApplyTheme; base.Activate(); UpdateCount();
    }
    public override async Task RefreshAsync()
    {
        Message(IsRunning ? "" : InactiveMessage);
        if (!IsRunning) { StopStream(); return; }
        var source = ControllerIdentity;
        var config = await Get("configs");
        if (!IsActive || !IsRunning || source != ControllerIdentity) return;
        controller = source;
        defaultLevel = config.Text("log-level", fallback: "info");
        RestartStream();
    }
    protected override Task LiveAsync(CancellationToken cancellationToken) { RestartStream(); return Task.CompletedTask; }
    private void RestartStream()
    {
        string selected = level.SelectedValue as string ?? ""; selected = selected.Length == 0 ? defaultLevel : selected;
        if (!IsActive || !IsRunning || selected == "silent") { StopStream(); return; }
        if (stream is not null && streamLevel == selected && streamController == ControllerIdentity) return;
        StopStream();
        stream = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        streamLevel = selected; streamController = ControllerIdentity;
        var token = stream.Token;
        _ = RunAsync(() => WatchStreamAsync("logs?level=" + Uri.EscapeDataString(selected), Receive, token));
    }
    private void StopStream() { stream?.Cancel(); stream?.Dispose(); stream = null; streamLevel = streamController = ""; }
    public override void Deactivate()
    {
        Session.ThemeChanged -= ApplyTheme; StopStream();
        if (clearing) FinishClear();
        noticeExit.Stop(); pausedLamp.BeginAnimation(OpacityProperty, null);
        noticeShown = false; pausedNotice.Visibility = Visibility.Collapsed;
        base.Deactivate();
    }
    protected override void OnLanguageChanged() { UpdateCount(); base.OnLanguageChanged(); }
    protected override void OnStatusChanged()
    {
        var current = ControllerIdentity;
        if (controller != current)
        {
            controller = current; StopStream(); history.Clear(); held.Clear();
            if (clearing) FinishClear(); else lines.Clear();
            pause.IsChecked = false; SetFollow(true); UpdateCount();
        }
        if (!IsRunning) StopStream();
        Message(IsRunning ? "" : InactiveMessage);
        UpdateCount();
    }
    private void Receive(JsonElement data)
    {
        var item = new LogRow(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), data.Text("type").ToUpperInvariant(), data.Text("payload"));
        if (pause.IsChecked == true) { held.Enqueue(item); while (held.Count > Capacity) held.Dequeue(); }
        else Add(item);
        UpdateCount();
    }
    private void Add(LogRow item)
    {
        if (history.Count > 0) history[^1].IsNewest = false;
        item.ArrivedAt = Stopwatch.GetTimestamp(); item.IsNewest = true; history.Add(item); if (history.Count > Capacity) history.RemoveAt(0);
        if (clearing) return;
        lines.Add(item); while (lines.Count > Capacity) lines.RemoveAt(0);
        if (followLatest) ScrollToLatest();
    }
    private void UpdateCount()
    {
        count.Inlines.Clear();
        var reading = new Run(lines.Count.ToString("N0")) { FontSize = 12 };
        reading.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
        Typography.SetNumeralAlignment(reading, FontNumeralAlignment.Tabular);
        count.Inlines.Add(reading); count.Inlines.Add(new Run(" " + L.T("条日志")));
        AutomationProperties.SetHelpText(count, L.F("{0:N0} 行 / 最多 {1:N0}", lines.Count, Capacity));
        bool paused = pause.IsChecked == true;
        if (pauseLabelState != paused)
        {
            pauseLabelState = paused;
            pause.Content = FluentControls.ButtonContent(paused ? "play" : "pause", paused ? "继续" : "暂停");
            L.Bind(pause, AutomationProperties.NameProperty, paused ? "继续" : "暂停");
        }
        heldCount.Text = held.Count.ToString("N0"); heldLabel.Visibility = held.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (held.Count != previousHeld && held.Count > 0 && FluentMotion.Enabled)
        {
            var movement = new TranslateTransform(); heldCount.RenderTransform = movement;
            SettingsSegments.Animate(heldCount, OpacityProperty, 0, 1, 250); SettingsSegments.Animate(movement, TranslateTransform.YProperty, 3, 0, 250);
        }
        previousHeld = held.Count;
        empty.Visibility = lines.Count == 0 && !paused ? Visibility.Visible : Visibility.Collapsed;
        clear.IsEnabled = !clearing && (lines.Count > 0 || held.Count > 0);
        UpdateNotice();
    }
    public sealed class LogRow(string time, string level, string message) : INotifyPropertyChanged
    {
        public string Time { get; } = time;
        public string Level { get; } = level;
        public string Message { get; } = message;
        public long ArrivedAt { get; internal set; } = Stopwatch.GetTimestamp();
        private bool newest;
        public bool IsNewest { get => newest; internal set { if (newest == value) return; newest = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNewest))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => $"{Time}  {Level}  {Message}";
    }
}
