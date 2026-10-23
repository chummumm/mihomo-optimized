using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Mihomo.Desktop.Controls;

public enum TransientMessageKind { Info, Success, Warning, Error }

/// <summary>The supplied Message surface: independently timed, overlapping bottom notices.</summary>
public sealed class TransientMessageHost : Grid, IDisposable
{
    private readonly List<Message> messages = [];
    private bool subscribed, disposed;

    public string LastMessageText { get; private set; } = "";
    public bool IsMessageVisible => messages.Count > 0;
    public string VisibleMessageText => messages.Count > 0 ? messages[^1].Text : "";
    public TransientMessageKind? VisibleMessageKind => messages.Count > 0 ? messages[^1].Kind : null;
    public bool IsErrorVisible => VisibleMessageKind == TransientMessageKind.Error;

    public TransientMessageHost()
    {
        Focusable = false;
        ClipToBounds = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        SetZIndex(this, 60);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        IsVisibleChanged += OnVisibilityChanged;
    }

    /// <summary>Text is already localized by the caller. The returned action removes only this message.</summary>
    public Action Show(string text, TransientMessageKind kind = TransientMessageKind.Info,
        double durationMilliseconds = 1500, Action? onClose = null)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(text);
        double duration = double.IsFinite(durationMilliseconds) ? Math.Clamp(durationMilliseconds, 0, int.MaxValue - 250d) : 1500;
        var message = new Message(this, text, kind, duration, onClose);
        messages.Add(message);
        LastMessageText = text;
        message.ApplyTheme();
        message.Place(ActualWidth);
        UpdateAutomationTarget();
        Children.Add(message.Surface);
        return () =>
        {
            if (Dispatcher.CheckAccess()) Remove(message);
            else if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.InvokeAsync(() => Remove(message));
        };
    }

    public Action Info(string text, double durationMilliseconds = 1500, Action? onClose = null) => Show(text, TransientMessageKind.Info, durationMilliseconds, onClose);
    public Action Success(string text, double durationMilliseconds = 1500, Action? onClose = null) => Show(text, TransientMessageKind.Success, durationMilliseconds, onClose);
    public Action Warning(string text, double durationMilliseconds = 1500, Action? onClose = null) => Show(text, TransientMessageKind.Warning, durationMilliseconds, onClose);
    public Action Error(string text, double durationMilliseconds = 1500, Action? onClose = null) => Show(text, TransientMessageKind.Error, durationMilliseconds, onClose);

    /// <summary>Remove all notices, or only those matching a resolved condition's exact text.</summary>
    public void Clear(string? matchingText = null)
    {
        Dispatcher.VerifyAccess();
        foreach (var message in messages.ToArray())
            if (matchingText is null || message.Text == matchingText) Remove(message);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (disposed) return;
        if (!subscribed)
        {
            subscribed = true;
            DesignTokens.Changed += ApplyTheme;
            SystemParameters.StaticPropertyChanged += OnSystemSettingChanged;
            LayoutUpdated += OnLayoutUpdated;
        }
        ApplyTheme();
        PlaceMessages();
        RefreshBackdrops();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        // Moving this one host above an owned modal must not restart or discard notices.
        Unsubscribe();
        foreach (var message in messages) message.DetachBackdrop();
    }

    private void Unsubscribe()
    {
        if (!subscribed) return;
        subscribed = false;
        DesignTokens.Changed -= ApplyTheme;
        SystemParameters.StaticPropertyChanged -= OnSystemSettingChanged;
        LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => PlaceMessages();
    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (IsVisible) RefreshBackdrops();
    }
    private void OnLayoutUpdated(object? sender, EventArgs args) => RefreshBackdrops();
    private void RefreshBackdrops()
    {
        if (disposed || !IsLoaded || !IsVisible || messages.Count == 0 || Parent is not Panel surface) return;
        Panel background = surface;
        var sources = new List<FrameworkElement>();
        if (Window.GetWindow(this) is FluentDialog { Owner.Content: Panel ownerSurface })
        {
            // An owned modal is a separate HWND. Explicitly composite the owner
            // below its smoke/card; neither complete root is sampled as a visual.
            background = ownerSurface;
            sources.AddRange(LowerLayers(ownerSurface, null));
        }
        sources.AddRange(LowerLayers(surface, this));
        var layers = sources.ToArray();
        foreach (var message in messages) message.AttachBackdrop(surface, background, layers);
    }
    private static IEnumerable<FrameworkElement> LowerLayers(Panel panel, TransientMessageHost? boundary)
    {
        int boundaryIndex = boundary is null ? int.MaxValue : panel.Children.IndexOf(boundary);
        int boundaryZ = boundary is null ? int.MaxValue : GetZIndex(boundary);
        return panel.Children.Cast<UIElement>()
            .Select((element, index) => (element, index, z: GetZIndex(element)))
            .Where(item => item.element is FrameworkElement and not TransientMessageHost
                && (item.z < boundaryZ || item.z == boundaryZ && item.index < boundaryIndex))
            .OrderBy(item => item.z).ThenBy(item => item.index)
            .Select(item => (FrameworkElement)item.element);
    }
    private void PlaceMessages()
    {
        foreach (var message in messages) message.Place(ActualWidth);
    }

    private void ApplyTheme()
    {
        foreach (var message in messages) message.ApplyTheme();
    }

    private void OnSystemSettingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(SystemParameters.ClientAreaAnimation) or nameof(SystemParameters.HighContrast))) return;
        if (!FluentMotion.Enabled)
            foreach (var message in messages) message.StopTranslation();
        ApplyTheme();
    }

    private void Remove(Message message)
    {
        if (!messages.Remove(message)) return;
        message.Stop();
        Children.Remove(message.Surface);
        UpdateAutomationTarget();
        message.OnClose?.Invoke();
    }

    private void UpdateAutomationTarget()
    {
        for (int i = 0; i < messages.Count; i++)
            AutomationProperties.SetAutomationId(messages[i].Content, i == messages.Count - 1 ? "MainError" : "");
    }

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        Unsubscribe();
        Clear();
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
        IsVisibleChanged -= OnVisibilityChanged;
    }

    private sealed class Message
    {
        private readonly TransientMessageHost host;
        private readonly FluentIcon icon;
        private readonly FluentBackdropFilter backdrop = new() { CornerRadius = new CornerRadius(8), Sigma = 30, Saturation = 1.5 };
        private readonly DropShadowEffect shadow = new()
        {
            Color = Colors.Black, BlurRadius = 16, ShadowDepth = 8, Direction = 270,
            RenderingBias = RenderingBias.Quality,
        };
        private readonly TranslateTransform movement = new();
        private readonly DispatcherTimer leave;
        private readonly DispatcherTimer fallback;
        private bool started, closing, removed;
        private Panel? sourceRoot, backgroundRoot;
        private FrameworkElement[] sourceLayers = [];

        internal string Text { get; }
        internal TransientMessageKind Kind { get; }
        internal Action? OnClose { get; }
        internal Border Surface { get; }
        internal TextBlock Content { get; }

        internal Message(TransientMessageHost host, string text, TransientMessageKind kind, double duration, Action? onClose)
        {
            this.host = host; Text = text; Kind = kind; OnClose = onClose;
            icon = new FluentIcon(kind switch
            {
                TransientMessageKind.Success => "check-circle", TransientMessageKind.Warning => "warning",
                TransientMessageKind.Error => "error", _ => "info",
            }, 16) { Filled = true, Margin = new Thickness(0, 2, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            Content = new TextBlock
            {
                Text = text, FontSize = 14, LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top,
            };
            Content.SetResourceReference(TextBlock.FontFamilyProperty, "BodyFont");
            Content.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            AutomationProperties.SetLiveSetting(Content, kind == TransientMessageKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.Children.Add(icon); Grid.SetColumn(Content, 1); content.Children.Add(Content);
            var foreground = new Border
            {
                Child = content, Background = Brushes.Transparent, CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), BorderThickness = new Thickness(1),
            };
            foreground.SetResourceReference(Border.BorderBrushProperty, "AcrylicStrokeBrush");
            var layers = new Grid();
            AutomationProperties.SetAutomationId(backdrop, "MessageBackdrop");
            layers.Children.Add(backdrop); layers.Children.Add(foreground);
            Surface = new Border
            {
                Child = layers, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 24),
                RenderTransform = movement, Opacity = 0,
            };
            Surface.Loaded += Loaded;
            leave = new DispatcherTimer(DispatcherPriority.Normal, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(duration) };
            fallback = new DispatcherTimer(DispatcherPriority.Normal, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(duration + 250) };
            leave.Tick += Leave;
            fallback.Tick += Fallback;
        }

        internal void AttachBackdrop(Panel root, Panel background, FrameworkElement[] sources)
        {
            if (removed || ReferenceEquals(sourceRoot, root) && ReferenceEquals(backgroundRoot, background) && sourceLayers.SequenceEqual(sources)) return;
            sourceRoot = root; backgroundRoot = background; sourceLayers = sources;
            backdrop.SetBinding(FluentBackdropFilter.BaseBrushProperty, new Binding(nameof(Panel.Background)) { Source = background, Mode = BindingMode.OneWay });
            backdrop.SetSources(root, sources);
        }

        internal void DetachBackdrop()
        {
            if (removed || sourceRoot is null) return;
            sourceRoot = backgroundRoot = null; sourceLayers = [];
            BindingOperations.ClearBinding(backdrop, FluentBackdropFilter.BaseBrushProperty);
            backdrop.SetResourceReference(FluentBackdropFilter.BaseBrushProperty, "AppBackgroundBrush");
            backdrop.SetSources(host, []);
        }

        internal void Place(double viewportWidth)
        {
            if (viewportWidth <= 0) return;
            bool narrow = viewportWidth <= 640;
            Surface.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            Surface.Margin = new Thickness(narrow ? 12 : 0, 0, narrow ? 12 : 0, 24);
            Surface.MaxWidth = narrow ? double.PositiveInfinity : Math.Min(480, Math.Max(0, viewportWidth - 32));
        }

        internal void ApplyTheme()
        {
            var acrylic = Color("AcrylicBrush");
            string? fill = Kind switch
            {
                TransientMessageKind.Success => "SuccessBackgroundBrush", TransientMessageKind.Warning => "WarningBackgroundBrush",
                TransientMessageKind.Error => "CriticalBackgroundBrush", _ => null,
            };
            backdrop.TintBrush = new SolidColorBrush(fill is null ? acrylic : Mix(Color(fill), acrylic, .7));
            icon.SetResourceReference(Control.ForegroundProperty, Kind switch
            {
                TransientMessageKind.Success => "SuccessBrush", TransientMessageKind.Warning => "WarningBrush",
                TransientMessageKind.Error => "DangerBrush", _ => "AccentBrush",
            });
            shadow.Opacity = Color("AppBackgroundBrush").R < 128 ? .26 : .14;
            Surface.Effect = SystemParameters.HighContrast ? null : shadow;
        }

        private Color Color(string key) => host.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Transparent;

        private static Color Mix(Color first, Color second, double firstWeight)
        {
            // CSS color-mix(in srgb) interpolates premultiplied channels, including the acrylic alpha.
            double firstAlpha = first.A / 255d * firstWeight, secondAlpha = second.A / 255d * (1 - firstWeight);
            double alpha = firstAlpha + secondAlpha;
            if (alpha <= 0) return Colors.Transparent;
            byte Channel(byte a, byte b) => (byte)Math.Clamp(Math.Round((a * firstAlpha + b * secondAlpha) / alpha), 0, 255);
            return System.Windows.Media.Color.FromArgb((byte)Math.Clamp(Math.Round(alpha * 255), 0, 255),
                Channel(first.R, second.R), Channel(first.G, second.G), Channel(first.B, second.B));
        }

        private void Loaded(object sender, RoutedEventArgs args)
        {
            if (removed) return;
            host.RefreshBackdrops();
            if (started) return;
            started = true;
            Surface.Opacity = 1;
            Surface.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FluentMotion.Fast) { FillBehavior = FillBehavior.Stop });
            if (FluentMotion.Enabled)
                movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, FluentMotion.Normal)
                { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
            leave.Start(); fallback.Start();
            if (UIElementAutomationPeer.CreatePeerForElement(Content) is { } peer)
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        private void Leave(object? sender, EventArgs args)
        {
            leave.Stop();
            if (closing || removed) return;
            closing = true;
            double from = Surface.Opacity;
            Surface.BeginAnimation(UIElement.OpacityProperty, null);
            Surface.Opacity = 0;
            var opacity = new DoubleAnimation(from, 0, FluentMotion.Fast) { FillBehavior = FillBehavior.Stop };
            opacity.Completed += (_, _) => host.Remove(this);
            Surface.BeginAnimation(UIElement.OpacityProperty, opacity);
            if (FluentMotion.Enabled)
            {
                double current = movement.Y;
                movement.BeginAnimation(TranslateTransform.YProperty, null);
                movement.Y = 12;
                movement.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(current, 12, FluentMotion.Normal)
                { EasingFunction = FluentMotion.Accelerate, FillBehavior = FillBehavior.Stop });
            }
        }

        private void Fallback(object? sender, EventArgs args) => host.Remove(this);

        internal void StopTranslation()
        {
            movement.BeginAnimation(TranslateTransform.YProperty, null);
            movement.Y = 0;
        }

        internal void Stop()
        {
            if (removed) return;
            removed = true;
            leave.Stop(); fallback.Stop();
            leave.Tick -= Leave; fallback.Tick -= Fallback;
            Surface.Loaded -= Loaded;
            Surface.BeginAnimation(UIElement.OpacityProperty, null);
            StopTranslation();
            backdrop.Dispose();
            BindingOperations.ClearBinding(backdrop, FluentBackdropFilter.BaseBrushProperty);
            sourceRoot = backgroundRoot = null; sourceLayers = [];
        }
    }
}
