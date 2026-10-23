using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>The real title-bar AutoSuggestBox input. Suggestions are a separate owned window.</summary>
public sealed class GlobalSearchBox : Grid
{
    private readonly TextBlock placeholder = Ui.Hint("搜索节点、规则、设置或命令", 14);
    private readonly Border shortcut;
    private bool showShortcut = true;
    private int suppressOpen;
    private int openRequests, suppressedRequests, palettesCreated, palettesClosed;
    private int activePalette;
    private string lastOpenReason = "none";
    internal TextBox Input { get; } = new()
    {
        Height = 32, FontSize = 14, MaxLength = 2048,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(11, 0, 88, 0)
    };
    internal event Action? OpenRequested;
    internal bool InputFocused => Input.IsKeyboardFocusWithin;
    internal bool ShowShortcut
    {
        get => showShortcut;
        set { if (showShortcut == value) return; showShortcut = value; UpdateChrome(); }
    }

    public GlobalSearchBox()
    {
        Height = 32; MaxWidth = 380;
        this.Id("GlobalSearch"); L.Bind(this, AutomationProperties.NameProperty, "搜索");
        Input.Id("SearchInput"); L.Bind(Input, AutomationProperties.NameProperty, "搜索");
        Children.Add(Input);
        placeholder.TextWrapping = TextWrapping.NoWrap; placeholder.TextTrimming = TextTrimming.CharacterEllipsis;
        placeholder.Margin = new Thickness(12, 0, 88, 0); placeholder.IsHitTestVisible = false; Children.Add(placeholder);
        var key = Ui.Text("Ctrl+K", 11); key.LineHeight = 16; key.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        key.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
        shortcut = new Border
        {
            Child = key, Padding = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 32, 0), IsHitTestVisible = false
        };
        shortcut.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); Children.Add(shortcut);
        var glyph = new FluentIcon("search", 16)
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 10, 0), IsHitTestVisible = false
        };
        glyph.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush"); Children.Add(glyph);
        Input.GotKeyboardFocus += (_, _) => RequestOpen("focus");
        Input.PreviewMouseLeftButtonDown += (_, _) => RequestOpen("pointer");
        Input.TextChanged += (_, _) =>
        {
            UpdateChrome();
            if (InputFocused) RequestOpen("text");
        };
        UpdateChrome();
    }

    internal void FocusInput()
    {
        if (Window.GetWindow(this) is { IsActive: false } window) window.Activate();
        Input.Focus();
    }
    internal void ClearQuery()
    {
        suppressOpen++;
        try { Input.Clear(); }
        finally { suppressOpen--; }
    }
    internal void BlurInput()
    {
        suppressOpen++;
        try
        {
            // An inactive owner can still remember this input as its logical
            // focus. Clear that reference even when keyboard focus is elsewhere.
            var scope = FocusManager.GetFocusScope(Input);
            if (ReferenceEquals(FocusManager.GetFocusedElement(scope), Input))
                FocusManager.SetFocusedElement(scope, null);
            if (InputFocused) Keyboard.ClearFocus();
        }
        finally { suppressOpen--; }
    }
    internal void DismissPalette(Action close, bool clearQuery, bool blur)
    {
        // Closing an owned HWND can restore focus to its owner's input after
        // Closed handlers have released the old palette. Keep passive opening
        // suppressed through that restoration, not just through TextBox.Clear.
        suppressOpen++;
        try
        {
            if (clearQuery) ClearQuery();
            if (blur) BlurInput();
            close();
            if (blur) BlurInput();
        }
        finally
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) suppressOpen--;
            else _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                try { if (blur) BlurInput(); }
                finally { suppressOpen--; }
            }));
        }
    }
    private void RequestOpen(string reason)
    {
        if (suppressOpen > 0) { suppressedRequests++; return; }
        openRequests++; lastOpenReason = reason; OpenRequested?.Invoke();
    }
    internal int PaletteCreated() => activePalette = ++palettesCreated;
    internal void PaletteClosed(int instance) { palettesClosed++; if (activePalette == instance) activePalette = 0; }
    internal object Describe() => new { inputFocused = InputFocused, queryLength = Input.Text.Length,
        suppressOpen, openRequests, suppressedRequests, lastOpenReason, palettesCreated, palettesClosed, activePalette };
    private void UpdateChrome()
    {
        bool blank = Input.Text.Length == 0;
        placeholder.Visibility = blank ? Visibility.Visible : Visibility.Collapsed;
        shortcut.Visibility = blank && ShowShortcut ? Visibility.Visible : Visibility.Collapsed;
        // The reference keeps the key element in the DOM at narrow widths;
        // its :has() padding therefore remains 88 while the query is empty.
        Input.Padding = new Thickness(11, 0, blank ? 88 : 34, 0);
        placeholder.Margin = new Thickness(12, 0, 88, 0);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SearchPeer(this);
    private sealed class SearchPeer(GlobalSearchBox owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => "AutoSuggestBox";
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        public void Invoke() => owner.Dispatcher.Invoke(() =>
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.FocusInput(); owner.RequestOpen("automation");
        });
    }
}
