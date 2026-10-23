using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal enum DialogDismissal { Immediate, StateToggle, Presence }

/// <summary>The reference ContentDialog: owner-sized smoke, a 448/640px card and equal-width footer commands.</summary>
internal sealed class FluentDialog : Window
{
    private static readonly HashSet<Window> pendingConfirmations = [];
    private readonly Grid mask = new();
    private readonly Grid notificationSurface = new();
    private readonly Border surface;
    private readonly DropShadowEffect ambientShadow = new() { Color = Colors.Black, BlurRadius = 96, ShadowDepth = 32, Direction = 270, RenderingBias = RenderingBias.Quality };
    // CSS blur 21 means sigma 10.5. WPF truncates the nominal radius 31.5 to 31 at 96 DPI.
    private readonly DropShadowEffect contactShadow = new() { Color = Colors.Black, BlurRadius = 31.5, ShadowDepth = 2, Direction = 270, RenderingBias = RenderingBias.Quality };
    private readonly ScaleTransform scale = new(1, 1);
    private readonly DialogDismissal dismissal;
    private bool completing, closed;
    internal bool IsClosed => closed;
    internal Panel NotificationSurface => notificationSurface;
    internal Func<bool>? CanDismiss { get; set; }
    internal FluentDialog(Window owner, string title, UIElement body, IReadOnlyList<Button> actions, bool large = false, DialogDismissal dismissal = DialogDismissal.Immediate)
    {
        this.dismissal = dismissal;
        Owner = owner; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var client = owner.Content as FrameworkElement ?? owner;
        var origin = client.PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(client);
        Width = client.ActualWidth; Height = client.ActualHeight;
        Left = origin.X / dpi.DpiScaleX; Top = origin.Y / dpi.DpiScaleY;
        L.Bind(this, TitleProperty, title); SetResourceReference(FontFamilyProperty, "BodyFont"); FontSize = 14;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        mask.SetResourceReference(Panel.BackgroundProperty, "ScrimBrush");
        bool narrow = Width <= 640; double padding = narrow ? 16 : 24;
        bool hasFooter = actions.Count > 0;
        var content = new StackPanel();
        var heading = Ui.Label(title, 20, true); heading.LineHeight = 28; heading.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont"); heading.Margin = new Thickness(0, 0, 0, 12);
        content.Children.Add(heading);
        var bodyScroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(80, Height - 32 - 2 * padding - 40 - (hasFooter ? 2 * padding + 33 : 0)) };
        content.Children.Add(bodyScroll);
        var top = new Border { Child = content, Padding = new Thickness(padding), CornerRadius = hasFooter ? new CornerRadius(7, 7, 0, 0) : new CornerRadius(7) }; top.SetResourceReference(Border.BackgroundProperty, "DialogTopBrush");
        var footer = new Grid();
        for (int i = 0; i < actions.Count; i++)
        {
            if (i > 0) footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            var button = actions[i]; button.Margin = new Thickness(0); button.Height = 32; button.Padding = new Thickness(12, 0, 12, 0); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(button, i * 2); footer.Children.Add(button);
        }
        var bottom = new Border { Child = footer, Padding = new Thickness(padding), BorderThickness = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(0, 0, 7, 7) };
        bottom.SetResourceReference(Border.BorderBrushProperty, "DividerBrush"); bottom.SetResourceReference(Border.BackgroundProperty, "DialogBottomBrush");
        var stack = new StackPanel(); stack.Children.Add(top); if (hasFooter) stack.Children.Add(bottom);
        var card = new Border { Child = stack, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "DialogBottomBrush"); card.SetResourceReference(Border.BorderBrushProperty, "AcrylicStrokeBrush");
        var layers = new Grid();
        // Independent casters reproduce the two CSS box shadows without shadowing one another.
        foreach (var effect in new[] { contactShadow, ambientShadow })
        {
            var caster = new Border { CornerRadius = new CornerRadius(8), Effect = effect, IsHitTestVisible = false };
            caster.SetResourceReference(Border.BackgroundProperty, "DialogBottomBrush"); layers.Children.Add(caster);
        }
        layers.Children.Add(card);
        surface = new Border { Child = layers, Width = Math.Min(large ? 640 : 448, Math.Max(280, Width - 32)), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, RenderTransformOrigin = new Point(.5, .5), RenderTransform = scale };
        mask.Children.Add(surface); notificationSurface.Children.Add(mask); Content = notificationSurface;
        mask.MouseDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, mask)) Complete(false); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Complete(false); } };
        Closing += (_, e) => { if (!completing && CanDismiss?.Invoke() == false) e.Cancel = true; };
        Closed += (_, _) =>
        {
            closed = true; DesignTokens.Changed -= UpdateShadows;
            mask.BeginAnimation(OpacityProperty, null); surface.BeginAnimation(OpacityProperty, null);
            if (Owner is MainWindow main) main.RestoreNotifications();
        };
        Loaded += (_, _) =>
        {
            if (Owner is MainWindow main) main.AttachNotifications(notificationSurface);
            DesignTokens.Changed += UpdateShadows; UpdateShadows();
            if (!FluentMotion.Enabled) return;
            if (dismissal == DialogDismissal.StateToggle) mask.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FluentMotion.Fast) { FillBehavior = FillBehavior.Stop });
            surface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
            if (Width > 768)
                foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty }) scale.BeginAnimation(property, new DoubleAnimation(1.05, 1, FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
        };
    }
    private void UpdateShadows()
    {
        bool dark = TryFindResource("DialogBottomBrush") is SolidColorBrush background && background.Color.R < 128;
        ambientShadow.Opacity = dark ? .37 : .1876;
        contactShadow.Opacity = dark ? .37 : .1474;
    }
    internal void Complete(bool accepted)
    {
        if (closed || completing || !accepted && CanDismiss?.Invoke() == false) return; completing = true;
        // TUN/import/exit are conditionally unmounted in the supplied source.
        // Persistent show=false and AnimatePresence dialogs have separate exits.
        if (dismissal == DialogDismissal.Immediate || !FluentMotion.Enabled) { DialogResult = accepted; return; }
        var fade = new DoubleAnimation(mask.Opacity, 0, dismissal == DialogDismissal.Presence ? TimeSpan.FromMilliseconds(200) : FluentMotion.Fast);
        var contentFade = new DoubleAnimation(surface.Opacity, 0, FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate };
        if (dismissal == DialogDismissal.Presence) contentFade.Completed += (_, _) => { if (!closed) DialogResult = accepted; };
        else fade.Completed += (_, _) => { if (!closed) DialogResult = accepted; };
        mask.BeginAnimation(OpacityProperty, fade);
        surface.BeginAnimation(OpacityProperty, contentFade);
        if (Width > 768)
            foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty }) scale.BeginAnimation(property, new DoubleAnimation(1.05, FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate });
    }
    internal static async Task<bool> ConfirmAsync(Window owner, string title, string message, string acceptLabel, bool destructive = false)
    {
        if (!pendingConfirmations.Add(owner)) return false;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!owner.IsLoaded) return false;
            var accept = new Button { Content = Ui.Label(acceptLabel), IsDefault = true };
            if (!destructive) FluentControls.Primary(accept); else FluentControlState.SetKind(accept, "Danger");
            var cancel = new Button { Content = Ui.Label("取消") };
            var dialog = new FluentDialog(owner, title, Ui.Label(message), [accept, cancel], dismissal: DialogDismissal.StateToggle);
            accept.Click += (_, _) => dialog.Complete(true); cancel.Click += (_, _) => dialog.Complete(false);
            dialog.Loaded += (_, _) => cancel.Focus(); return dialog.ShowDialog() == true;
        }
        finally { pendingConfirmations.Remove(owner); }
    }
}
