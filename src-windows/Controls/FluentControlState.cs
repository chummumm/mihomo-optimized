using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mihomo.Desktop.Controls;

/// <summary>Interruptible native control states using the supplied CSS timings, without replacing native automation peers.</summary>
public static class FluentControlState
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(string), typeof(FluentControlState), new PropertyMetadata("", Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(FluentControlState));
    public static string GetKind(DependencyObject item) => (string)item.GetValue(KindProperty);
    public static void SetKind(DependencyObject item, string value) => item.SetValue(KindProperty, value);
    private static void Changed(DependencyObject item, DependencyPropertyChangedEventArgs args)
    {
        if (item is not Control control) return;
        if (control.GetValue(StateProperty) is State old) old.Dispose();
        var state = new State(control); control.SetValue(StateProperty, state);
    }

    private sealed class State : IDisposable
    {
        private readonly Control control;
        private readonly List<(DependencyPropertyDescriptor Descriptor, EventHandler Handler)> subscriptions = [];
        private bool attached;
        private bool ready;
        private Popup? popup;
        private int popupGeneration;
        internal State(Control control)
        {
            this.control = control;
            control.Loaded += Loaded; control.Unloaded += Unloaded;
            if (control.IsLoaded) Attach();
        }
        private void Loaded(object sender, RoutedEventArgs e) => Attach();
        private void Unloaded(object sender, RoutedEventArgs e) => Detach();
        private void Attach()
        {
            if (attached) return;
            attached = true; control.ApplyTemplate();
            control.MouseEnter += Mouse; control.MouseLeave += Mouse;
            control.IsEnabledChanged += Enabled;
            control.GotKeyboardFocus += Focus; control.LostKeyboardFocus += Focus;
            if (control is ButtonBase) Watch(ButtonBase.IsPressedProperty);
            if (control is ToggleButton) Watch(ToggleButton.IsCheckedProperty);
            if (GetKind(control) == "HomeMore") Watch(FrameworkElement.TagProperty);
            if (control is ComboBox combo)
            {
                Watch(ComboBox.IsDropDownOpenProperty);
                popup = control.Template?.FindName("PART_Popup", control) as Popup;
                if (popup is not null)
                {
                    popup.PlacementTarget = combo;
                    popup.CustomPopupPlacementCallback = (_, _, _) => [new CustomPopupPlacement(new Point(-16, -21 - Math.Min(Math.Max(0, combo.SelectedIndex), 8) * 34), PopupPrimaryAxis.Vertical)];
                    popup.Opened += PopupOpened;
                }
            }
            DesignTokens.Changed += ThemeChanged;
            Update(false); ready = true;
        }
        private void Watch(DependencyProperty property)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, control.GetType());
            EventHandler handler = (_, _) => { Update(ready); if (property == ComboBox.IsDropDownOpenProperty) ChangePopup(); };
            descriptor.AddValueChanged(control, handler); subscriptions.Add((descriptor, handler));
        }
        private void ThemeChanged() => Update(false);
        private void Mouse(object sender, MouseEventArgs e) => Update(ready);
        private void Focus(object sender, KeyboardFocusChangedEventArgs e) => Update(ready);
        private void Enabled(object sender, DependencyPropertyChangedEventArgs e) => Update(ready);
        private void Detach()
        {
            if (!attached) return;
            attached = ready = false; ++popupGeneration;
            control.MouseEnter -= Mouse; control.MouseLeave -= Mouse;
            control.IsEnabledChanged -= Enabled;
            control.GotKeyboardFocus -= Focus; control.LostKeyboardFocus -= Focus;
            DesignTokens.Changed -= ThemeChanged;
            foreach (var (descriptor, handler) in subscriptions) descriptor.RemoveValueChanged(control, handler);
            subscriptions.Clear();
            if (popup is not null) { popup.Opened -= PopupOpened; popup.IsOpen = false; popup = null; }
        }
        public void Dispose() { Detach(); control.Loaded -= Loaded; control.Unloaded -= Unloaded; }
        private Border? Part(string name) => control.Template?.FindName(name, control) as Border;
        private Color Color(string key) => control.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Transparent;
        private void Update(bool animate)
        {
            if (Part("PART_Frame") is not { } frame) return;
            string kind = GetKind(control);
            bool enabled = control.IsEnabled, hover = enabled && control.IsMouseOver;
            bool pressed = enabled && (control is ButtonBase button && button.IsPressed || control is ComboBox combo && combo.IsDropDownOpen);
            bool selected = control is ToggleButton toggle && toggle.IsChecked == true || kind == "HomeMore" && control.Tag is true;
            double milliseconds = kind == "Switch" ? 167 : 83;
            string fill = "ControlBrush", stroke = "ControlStrokeBrush", foreground = "TextPrimaryBrush";
            if (kind is "Switch" or "CheckBox")
            {
                fill = selected ? hover ? "AccentHoverBrush" : "AccentBrush" : hover ? "ControlAltHoverBrush" : "ControlAltBrush";
                stroke = selected ? "AccentBrush" : "ControlStrongBrush";
                if (!enabled) { fill = selected ? "TextDisabledBrush" : "Transparent"; stroke = selected ? "Transparent" : "TextDisabledBrush"; }
            }
            else if (kind == "HomeTile")
            {
                fill = pressed ? "SubtlePressedBrush" : hover ? selected ? "OnAccentHoverBrush" : "SubtleHoverBrush" : "Transparent";
                stroke = "Transparent";
                foreground = selected ? "AccentTextBrush" : "TextPrimaryBrush";
                if (!enabled) foreground = selected ? "AccentTextSecondaryBrush" : "TextDisabledBrush";
            }
            else if (kind == "HomeMore")
            {
                fill = hover ? selected ? "OnAccentHoverBrush" : "SubtleHoverBrush" : "Transparent";
                stroke = selected ? "OnAccentDividerBrush" : "DividerBrush";
                foreground = selected ? "AccentTextBrush" : "TextPrimaryBrush";
                if (!enabled) foreground = "TextDisabledBrush";
            }
            else if (kind == "Ghost")
            {
                fill = pressed ? "SubtlePressedBrush" : hover ? "SubtleHoverBrush" : "Transparent"; stroke = "Transparent";
                if (pressed) foreground = "TextSecondaryBrush";
                if (!enabled) { fill = "Transparent"; foreground = "TextDisabledBrush"; }
            }
            else if (kind == "Primary" || kind == "Toggle" && selected)
            {
                fill = pressed ? "AccentPressedBrush" : hover ? "AccentHoverBrush" : "AccentBrush";
                foreground = pressed ? "AccentTextSecondaryBrush" : "AccentTextBrush"; stroke = pressed || kind == "Toggle" ? "Transparent" : "AccentStrokeBrush";
                if (!enabled) { fill = "ControlDisabledBrush"; foreground = "TextDisabledBrush"; stroke = "Transparent"; }
            }
            else
            {
                fill = pressed ? "ControlPressedBrush" : hover ? "ControlHoverBrush" : "ControlBrush";
                if (kind == "Danger") foreground = "DangerBrush";
                if (kind == "Input" && control.IsKeyboardFocusWithin) fill = "ControlFocusBrush";
                if (pressed) foreground = "TextSecondaryBrush";
                if (!enabled) { fill = "ControlDisabledBrush"; foreground = "TextDisabledBrush"; }
            }
            Paint(frame, Border.BackgroundProperty, Color(fill), milliseconds, animate);
            if (frame.BorderThickness.Left > 0 || frame.BorderThickness.Top > 0) Paint(frame, Border.BorderBrushProperty, Color(stroke), milliseconds, animate);
            Paint(control, Control.ForegroundProperty, Color(foreground), milliseconds, animate);
            if (Part("PART_TopEdge") is { } top)
                Paint(top, Border.BackgroundProperty, Color(kind == "Ghost" ? "Transparent" : kind == "Primary" ? pressed || !enabled ? "Transparent" : "AccentStrokeBrush" : "ControlElevationTopBrush"), milliseconds, animate);
            if (Part("PART_BottomEdge") is { } bottom)
                Paint(bottom, Border.BackgroundProperty, Color(kind == "Ghost" ? "Transparent" : kind == "Primary" ? pressed || !enabled ? "Transparent" : "AccentElevationBrush" : pressed || !enabled ? "ControlStrokeBrush" : "ControlElevationBrush"), milliseconds, animate);
            if (kind == "Switch" && Part("PART_Thumb") is { } thumb)
            {
                double width = pressed ? 17 : hover ? 14 : 12, height = hover || pressed ? 14 : 12;
                // The CSS left coordinate is inside a 1px border.
                double x = (hover ? 3 : 4) + (selected ? pressed ? 17 : 20 : 0);
                Number(thumb, FrameworkElement.WidthProperty, width, animate);
                Number(thumb, FrameworkElement.HeightProperty, height, animate);
                if (thumb.RenderTransform is TranslateTransform transform) Number(transform, TranslateTransform.XProperty, x, animate);
                Paint(thumb, Border.BackgroundProperty, Color(enabled ? selected ? "AccentTextBrush" : "TextSecondaryBrush" : selected ? "AppBackgroundBrush" : "TextDisabledBrush"), milliseconds, animate);
            }
        }
        private static void Paint(DependencyObject target, DependencyProperty property, Color destination, double milliseconds, bool animate)
        {
            var from = target.GetValue(property) is SolidColorBrush previous ? previous.Color : destination;
            var brush = new SolidColorBrush(destination);
            target.SetCurrentValue(property, brush);
            if (animate && FluentMotion.Enabled && from != destination)
                brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, destination, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop });
        }
        private static void Number(DependencyObject target, DependencyProperty property, double destination, bool animate)
        {
            double from = (double)target.GetValue(property);
            if (target is Animatable animatable) animatable.BeginAnimation(property, null);
            else if (target is UIElement element) element.BeginAnimation(property, null);
            target.SetValue(property, destination);
            if (!animate || !FluentMotion.Enabled || !double.IsFinite(from) || Math.Abs(from - destination) < .001) return;
            var animation = new DoubleAnimation(from, destination, TimeSpan.FromMilliseconds(167)) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop };
            if (target is Animatable a) a.BeginAnimation(property, animation); else if (target is UIElement e) e.BeginAnimation(property, animation);
        }
        private void ChangePopup()
        {
            if (control is not ComboBox combo || popup is null) return;
            int generation = ++popupGeneration;
            if (combo.IsDropDownOpen)
            {
                bool reopening = popup.IsOpen;
                popup.IsOpen = true;
                if (reopening) PopupOpened(popup, EventArgs.Empty);
                return;
            }
            if (!popup.IsOpen) return;
            if (Part("PART_PopupFrame") is not { } frame || !FluentMotion.Enabled) { popup.IsOpen = false; return; }
            var fade = new DoubleAnimation(frame.Opacity, 0, FluentMotion.Faster);
            fade.Completed += (_, _) => { if (generation == popupGeneration && popup is not null && !combo.IsDropDownOpen) popup.IsOpen = false; };
            frame.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        private void PopupOpened(object? sender, EventArgs e)
        {
            if (control is not ComboBox combo || Part("PART_PopupFrame") is not { } frame) return;
            frame.BeginAnimation(UIElement.OpacityProperty, null); frame.Opacity = 1;
            frame.UpdateLayout();
            if (combo.ItemContainerGenerator.ContainerFromIndex(combo.SelectedIndex) is ComboBoxItem selected) selected.BringIntoView();
            if (!FluentMotion.Enabled) { frame.Clip = null; return; }
            double height = frame.ActualHeight, width = frame.ActualWidth;
            if (height <= 0 || width <= 0) return;
            double top = Math.Clamp(5 + Math.Max(0, combo.SelectedIndex) * 34, 0, Math.Max(0, height - 32));
            var clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8);
            frame.Clip = clip;
            var reveal = new RectAnimation(new Rect(0, top, width, Math.Min(32, height)), new Rect(0, 0, width, height), FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop };
            clip.BeginAnimation(RectangleGeometry.RectProperty, reveal);
            frame.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.6, 1, FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
        }
    }
}
