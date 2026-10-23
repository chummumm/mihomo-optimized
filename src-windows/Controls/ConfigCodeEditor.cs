using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Markup;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>A native, plain-text YAML surface with a virtual line-number gutter.</summary>
internal sealed class ConfigCodeEditor : UserControl
{
    public TextBox Editor { get; } = new()
    {
        AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12,
        Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(0), UndoLimit = 100
    };
    private readonly LineNumberGutter gutter;
    private readonly Border frame;
    private readonly Border underline = new() { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
    private readonly Grid empty = new() { Margin = new Thickness(46, 0, 0, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly FluentIcon emptyIcon = new("edit", 22);
    private readonly TextBlock emptyTitle = Ui.Label("配置编辑区", 14, true);
    private readonly TextBlock emptyHint = Ui.Hint("读取成功后，可在这里编辑当前内核的配置文件。");
    private bool held;
    private readonly WrapPanel findBar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(8) };
    private readonly TextBox find = Ui.SearchBox("在配置中查找", "ConfigFindInput");
    private readonly TextBlock findStatus = Ui.Muted("");
    private readonly CheckBox matchCase = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private int[] lineStarts = [0];
    private string lastAccepted = "";
    private bool restoring;
    private int maximumBytes = 4 * 1024 * 1024;
    public event Action? SaveRequested;
    public event Action? CloseRequested;
    public event Action? LimitExceeded;
    public event Action? PositionChanged;
    public int Line => FindLine(Editor.CaretIndex) + 1;
    public int Column => Editor.CaretIndex - lineStarts[FindLine(Editor.CaretIndex)] + 1;
    public int MaximumBytes { get => maximumBytes; set => maximumBytes = Math.Clamp(value, 1, 4 * 1024 * 1024); }

    public ConfigCodeEditor()
    {
        this.Id("ConfigCodeSurface"); Editor.Id("ConfigEditor");
        L.Bind(Editor, AutomationProperties.NameProperty, "配置文件编辑器");
        Editor.Style = null; Editor.Background = Brushes.Transparent;
        Editor.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="TextBox">
              <ScrollViewer x:Name="PART_ContentHost" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Margin="{TemplateBinding Padding}" Focusable="False" Background="Transparent"/>
            </ControlTemplate>
            """);
        Editor.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        TextBlock.SetLineHeight(Editor, 20.4); TextBlock.SetLineStackingStrategy(Editor, LineStackingStrategy.BlockLineHeight);
        SpellCheck.SetIsEnabled(Editor, false);
        gutter = new LineNumberGutter(this).Id("ConfigLineNumbers");
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition());
        L.Bind(matchCase, ContentControl.ContentProperty, "区分大小写");
        find.MinWidth = 200; find.Margin = new Thickness(0, 0, 8, 0);
        findBar.Children.Add(find); findBar.Children.Add(matchCase);
        findBar.Children.Add(FindButton("上一个", "ConfigFindPrevious", () => FindMatch(backwards: true)));
        findBar.Children.Add(FindButton("下一个", "ConfigFindNext", () => FindMatch()));
        findBar.Children.Add(FindButton("关闭查找", "ConfigFindClose", CloseFind));
        findStatus.Margin = new Thickness(8, 0, 0, 0); findStatus.Id("ConfigFindStatus"); findBar.Children.Add(findStatus);
        grid.Children.Add(findBar);
        var surface = new Grid(); surface.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); surface.ColumnDefinitions.Add(new ColumnDefinition());
        surface.Children.Add(gutter); Grid.SetColumn(Editor, 1); surface.Children.Add(Editor); Grid.SetRow(surface, 1); grid.Children.Add(surface);
        var emptyCopy = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        emptyIcon.SetResourceReference(Control.ForegroundProperty, "AccentLinkBrush"); emptyIcon.Margin = new Thickness(0, 0, 0, 8); emptyCopy.Children.Add(emptyIcon);
        emptyTitle.HorizontalAlignment = emptyHint.HorizontalAlignment = HorizontalAlignment.Center; emptyTitle.Margin = new Thickness(0, 0, 0, 8); emptyCopy.Children.Add(emptyTitle); emptyCopy.Children.Add(emptyHint); empty.Children.Add(emptyCopy); Grid.SetColumnSpan(empty, 2); surface.Children.Add(empty);
        var frameLayers = new Grid(); frameLayers.Children.Add(grid); underline.SetResourceReference(Border.BackgroundProperty, "ControlStrongBrush"); frameLayers.Children.Add(underline);
        frame = new Border { Child = frameLayers, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true };
        frame.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeBrush"); frame.SetResourceReference(Border.BackgroundProperty, "ControlBrush"); Content = frame;
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            frame.SetResourceReference(Border.BackgroundProperty, IsKeyboardFocusWithin ? "ControlFocusBrush" : "ControlBrush"); underline.Height = IsKeyboardFocusWithin ? 2 : 1; underline.SetResourceReference(Border.BackgroundProperty, IsKeyboardFocusWithin ? "AccentBrush" : "ControlStrongBrush");
        };
        Editor.TextChanged += TextChanged;
        Editor.SelectionChanged += (_, _) => { gutter.InvalidateVisual(); PositionChanged?.Invoke(); };
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => gutter.InvalidateVisual()));
        Editor.SizeChanged += (_, _) => gutter.InvalidateVisual();
        PreviewKeyDown += HandleKeyDown;
        find.TextChanged += (_, _) => FindMatch(includeCurrent: true);
        matchCase.Checked += (_, _) => FindMatch(includeCurrent: true); matchCase.Unchecked += (_, _) => FindMatch(includeCurrent: true);
        find.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { FindMatch((Keyboard.Modifiers & ModifierKeys.Shift) != 0); e.Handled = true; } };
        L.Changed += () => { gutter.InvalidateVisual(); if (findBar.Visibility == Visibility.Visible) UpdateFindStatus(false); };
        empty.Loaded += (_, _) => { if (empty.Visibility == Visibility.Visible) AnimateEmpty(); };
    }

    internal void SetAvailability(bool loaded, bool loading)
    {
        var wasVisible = empty.Visibility == Visibility.Visible; empty.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;
        L.Bind(emptyTitle, TextBlock.TextProperty, loading ? "正在读取…" : "配置编辑区");
        L.Bind(emptyHint, TextBlock.TextProperty, loading ? "正在读取当前控制器的配置文件。" : "读取成功后，可在这里编辑当前内核的配置文件。"); emptyIcon.Kind = loading ? "update" : "edit";
        empty.Margin = new Thickness((Window.GetWindow(this)?.ActualWidth ?? 1000) <= 768 ? 40 : 46, 0, 0, 0);
        if (loaded) { empty.BeginAnimation(OpacityProperty, null); empty.Opacity = 1; }
        else if (!wasVisible && IsLoaded) AnimateEmpty();
    }
    private void AnimateEmpty()
    {
        if (!ProxyVisuals.Animate) return;
        var move = new TranslateTransform(0, 28); empty.RenderTransform = move; empty.BeginAnimation(OpacityProperty, null); empty.Opacity = 0;
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = FluentMotion.Decelerate });
        empty.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(80), EasingFunction = FluentMotion.Decelerate });
    }
    internal void SetHeld(bool value)
    {
        if (held == value) return; held = value;
        var opacity = value ? .45 : 1;
        foreach (var element in new UIElement[] { Editor, gutter })
        {
            var before = element.Opacity; element.BeginAnimation(OpacityProperty, null); element.Opacity = opacity;
            if (ProxyVisuals.Animate) element.BeginAnimation(OpacityProperty, new DoubleAnimation(before, opacity, TimeSpan.FromMilliseconds(167)));
        }
    }

    private Button FindButton(string title, string id, Action action)
    {
        var button = L.Bind(new Button { MinHeight = 30, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 6, 0) }.Id(id), ContentControl.ContentProperty, title);
        L.Bind(button, AutomationProperties.NameProperty, title); button.Click += (_, _) => action(); return button;
    }

    private void TextChanged(object sender, TextChangedEventArgs e)
    {
        if (restoring) return;
        if (Encoding.UTF8.GetByteCount(DisplayText(Editor.Text)) > maximumBytes)
        {
            var caret = Math.Min(Editor.CaretIndex, lastAccepted.Length);
            restoring = true; Editor.Text = lastAccepted; Editor.CaretIndex = caret; restoring = false;
            LimitExceeded?.Invoke();
        }
        lastAccepted = Editor.Text;
        var count = 1;
        foreach (var character in lastAccepted) if (character == '\n') count++;
        lineStarts = new int[count]; var line = 1;
        for (var index = 0; index < lastAccepted.Length; index++) if (lastAccepted[index] == '\n') lineStarts[line++] = index + 1;
        gutter.InvalidateMeasure(); gutter.InvalidateVisual(); PositionChanged?.Invoke();
        if (findBar.Visibility == Visibility.Visible) UpdateFindStatus(false);
    }

    private void HandleKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.S && modifiers == ModifierKeys.Control) { e.Handled = true; SaveRequested?.Invoke(); }
        else if (e.Key == Key.F && modifiers == ModifierKeys.Control) { e.Handled = true; OpenFind(); }
        else if (e.Key == Key.F3) { e.Handled = true; if (findBar.Visibility != Visibility.Visible) OpenFind(); else FindMatch((modifiers & ModifierKeys.Shift) != 0); }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true; if (findBar.Visibility == Visibility.Visible) CloseFind(); else CloseRequested?.Invoke();
        }
        else if (e.Key == Key.Tab && modifiers == ModifierKeys.None && Editor.IsKeyboardFocusWithin && !Editor.IsReadOnly && Editor.IsEnabled)
        {
            // Match the supplied editor: a tab replaces the selection with two spaces.
            e.Handled = true; var start = Editor.SelectionStart;
            Editor.SelectedText = "  "; Editor.CaretIndex = Math.Min(start + 2, Editor.Text.Length);
        }
    }

    public void OpenFind()
    {
        findBar.Visibility = Visibility.Visible;
        var selected = Editor.SelectedText;
        if (selected.Length is > 0 and <= 256 && !selected.Contains('\n')) find.Text = selected;
        find.Focus(); find.SelectAll();
    }
    private void CloseFind() { findBar.Visibility = Visibility.Collapsed; Editor.Focus(); }

    private void FindMatch(bool backwards = false, bool includeCurrent = false)
    {
        var needle = find.Text; if (needle.Length == 0) { findStatus.Text = ""; return; }
        var text = Editor.Text; var comparison = matchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = includeCurrent ? Editor.SelectionStart : backwards ? Editor.SelectionStart - 1 : Editor.SelectionStart + Math.Max(1, Editor.SelectionLength);
        var index = -1; var wrapped = false;
        if (backwards)
        {
            if (text.Length > 0 && start >= 0) index = text.LastIndexOf(needle, Math.Min(start, text.Length - 1), comparison);
            if (index < 0 && text.Length > 0) { index = text.LastIndexOf(needle, comparison); wrapped = index >= 0; }
        }
        else
        {
            if (start <= text.Length) index = text.IndexOf(needle, Math.Max(0, start), comparison);
            if (index < 0) { index = text.IndexOf(needle, comparison); wrapped = index >= 0; }
        }
        if (index < 0) { findStatus.Text = L.T("未找到"); return; }
        Editor.Select(index, needle.Length); Editor.ScrollToLine(FindLine(index));
        var rect = Editor.GetRectFromCharacterIndex(index);
        if (!rect.IsEmpty) Editor.ScrollToHorizontalOffset(Math.Max(0, Editor.HorizontalOffset + rect.X - Editor.ActualWidth / 2));
        UpdateFindStatus(wrapped);
    }

    private void UpdateFindStatus(bool wrapped)
    {
        if (find.Text.Length == 0) { findStatus.Text = ""; return; }
        findStatus.Text = wrapped ? L.T("已循环查找") : L.F("第 {0} 行，第 {1} 列", Line, Column);
    }
    private int FindLine(int offset)
    {
        var index = Array.BinarySearch(lineStarts, Math.Clamp(offset, 0, Editor.Text.Length));
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    public void SetDocument(string content, bool resetPosition)
    {
        var selection = Editor.SelectionStart; var length = Editor.SelectionLength;
        var vertical = Editor.VerticalOffset; var horizontal = Editor.HorizontalOffset;
        var display = DisplayText(content);
        if (DisplayText(Editor.Text) != display) Editor.Text = display;
        if (resetPosition) { Editor.Select(0, 0); Editor.ScrollToHome(); Editor.ScrollToHorizontalOffset(0); }
        else { Editor.Select(Math.Min(selection, Editor.Text.Length), Math.Min(length, Math.Max(0, Editor.Text.Length - selection))); Editor.ScrollToVerticalOffset(vertical); Editor.ScrollToHorizontalOffset(horizontal); }
        gutter.InvalidateVisual(); PositionChanged?.Invoke();
    }
    public static string DisplayText(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    public static string SaveText(string content, string original) => original.Contains("\r\n", StringComparison.Ordinal) ? DisplayText(content).Replace("\n", "\r\n", StringComparison.Ordinal) : DisplayText(content);

    private sealed class LineNumberGutter(ConfigCodeEditor owner) : FrameworkElement
    {
        private int firstLine;
        private int lastLine;
        protected override Size MeasureOverride(Size availableSize)
        {
            var glyph = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(owner.Editor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), owner.Editor.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            return new Size(22 + Math.Max(2, owner.lineStarts.Length.ToString(CultureInfo.InvariantCulture).Length) * glyph.WidthIncludingTrailingWhitespace, 0);
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            var border = (Brush?)TryFindResource("DividerBrush") ?? Brushes.Gray;
            drawing.DrawLine(new Pen(border, 1), new Point(ActualWidth - .5, 0), new Point(ActualWidth - .5, ActualHeight));
            var text = owner.Editor;
            if (!text.IsVisible || text.ActualHeight <= 0) return;
            firstLine = Math.Max(0, text.GetFirstVisibleLineIndex()); lastLine = Math.Min(owner.lineStarts.Length - 1, Math.Max(firstLine, text.GetLastVisibleLineIndex()));
            var foreground = (Brush?)TryFindResource("TextTertiaryBrush") ?? Brushes.Gray;
            for (var line = firstLine; line <= lastLine; line++)
            {
                var rect = text.GetRectFromCharacterIndex(owner.lineStarts[line]);
                if (rect.IsEmpty) continue;
                var number = new FormattedText((line + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(text.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), text.FontSize, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                drawing.DrawText(number, new Point(ActualWidth - 11 - number.Width, rect.Y + Math.Max(0, (rect.Height - number.Height) / 2)));
            }
            AutomationProperties.SetName(this, L.F("行号 {0}–{1}", firstLine + 1, lastLine + 1));
        }
        protected override AutomationPeer OnCreateAutomationPeer() => new GutterPeer(this);
        private sealed class GutterPeer(LineNumberGutter owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override string GetClassNameCore() => "ConfigLineNumbers";
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        }
    }
}
