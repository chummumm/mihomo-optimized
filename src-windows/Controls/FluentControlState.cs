using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Mihomo.Desktop.Controls;

/// <summary>Interruptible native control states using the supplied CSS timings, without replacing native automation peers.</summary>
public static class FluentControlState
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached("Kind", typeof(string), typeof(FluentControlState), new PropertyMetadata("", Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(FluentControlState));
    public static string GetKind(DependencyObject item) => (string)item.GetValue(KindProperty);
    public static void SetKind(DependencyObject item, string value) => item.SetValue(KindProperty, value);
    public static readonly DependencyProperty MenuLeadingProperty = DependencyProperty.RegisterAttached("MenuLeading", typeof(bool), typeof(FluentControlState), new PropertyMetadata(false));
    public static bool GetMenuLeading(DependencyObject item) => (bool)item.GetValue(MenuLeadingProperty);
    public static void SetMenuLeading(DependencyObject item, bool value) => item.SetValue(MenuLeadingProperty, value);
    public static readonly DependencyProperty MenuCaptionProperty = DependencyProperty.RegisterAttached("MenuCaption", typeof(bool), typeof(FluentControlState), new PropertyMetadata(false));
    public static bool GetMenuCaption(DependencyObject item) => (bool)item.GetValue(MenuCaptionProperty);
    public static void SetMenuCaption(DependencyObject item, bool value) => item.SetValue(MenuCaptionProperty, value);
    public static readonly DependencyProperty MenuTextProperty = DependencyProperty.RegisterAttached("MenuText", typeof(bool), typeof(FluentControlState), new PropertyMetadata(false));
    public static bool GetMenuText(DependencyObject item) => (bool)item.GetValue(MenuTextProperty);
    public static void SetMenuText(DependencyObject item, bool value) => item.SetValue(MenuTextProperty, value);
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
        private PopupBackdrop? backdrop;
        private int popupGeneration;
        private bool menuOpen, menuPressed;
        private double menuX, menuY;
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
                    popup.CustomPopupPlacementCallback = (_, _, _) => PlacePopup(combo);
                    popup.Opened += PopupOpened;
                    popup.Closed += PopupClosed;
                }
            }
            if (control is ContextMenu menu)
            {
                menu.Opened += MenuOpened; menu.Closed += MenuClosed;
                if (menu.IsOpen) control.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (attached && menu.IsOpen) OpenMenu(menu); }));
            }
            if (control is MenuItem item)
            {
                SetMenuText(item, item.Header is string); Watch(MenuItem.IsHighlightedProperty); Watch(HeaderedItemsControl.HeaderProperty);
                item.PreviewMouseLeftButtonDown += MenuDown; item.PreviewMouseLeftButtonUp += MenuUp;
                popup = control.Template?.FindName("PART_Popup", control) as Popup;
                if (popup is not null) { popup.Opened += SubmenuOpened; popup.Closed += PopupClosed; }
            }
            DesignTokens.Changed += ThemeChanged;
            Update(false); UpdatePopupShadow(); ready = true;
        }
        private void Watch(DependencyProperty property)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(property, control.GetType());
            EventHandler handler = (_, _) => { if (control is MenuItem item && property == HeaderedItemsControl.HeaderProperty) SetMenuText(item, item.Header is string); Update(ready); if (property == ComboBox.IsDropDownOpenProperty) ChangePopup(); };
            descriptor.AddValueChanged(control, handler); subscriptions.Add((descriptor, handler));
        }
        private void ThemeChanged() { Update(false); UpdatePopupShadow(); }
        private void UpdatePopupShadow()
        {
            if (Part("PART_PopupFrame") is not { Effect: DropShadowEffect shadow } frame) return;
            if (shadow.IsFrozen) { shadow = shadow.CloneCurrentValue(); frame.Effect = shadow; }
            shadow.Opacity = SystemParameters.HighContrast ? 0 : Color("AppBackgroundBrush").R < 128 ? .26 : .14;
        }
        private void Mouse(object sender, MouseEventArgs e) { if (!control.IsMouseOver) menuPressed = false; Update(ready); }
        private void MenuDown(object sender, MouseButtonEventArgs e) { menuPressed = control.IsEnabled; Update(false); }
        private void MenuUp(object sender, MouseButtonEventArgs e) { menuPressed = false; Update(false); }
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
            if (control is ContextMenu menu) { menu.Opened -= MenuOpened; menu.Closed -= MenuClosed; ResetMenu(menu); }
            if (control is MenuItem item) { item.PreviewMouseLeftButtonDown -= MenuDown; item.PreviewMouseLeftButtonUp -= MenuUp; }
            if (popup is not null) { popup.Opened -= PopupOpened; popup.Opened -= SubmenuOpened; popup.Closed -= PopupClosed; popup.IsOpen = false; popup = null; }
            ReleaseBackdrop();
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
            if (control is MenuItem menuItem)
            {
                fill = !enabled ? "Transparent" : menuPressed ? "SubtlePressedBrush" : menuItem.IsHighlighted ? "SubtleHoverBrush" : "Transparent";
                stroke = "Transparent"; foreground = GetMenuCaption(menuItem) ? "TextSecondaryBrush" : !enabled ? "TextDisabledBrush" : menuPressed ? "TextSecondaryBrush" : "TextPrimaryBrush"; animate = false;
            }
            else if (kind is "Switch" or "CheckBox")
            {
                fill = selected ? hover ? "AccentHoverBrush" : "AccentBrush" : hover ? "ControlAltHoverBrush" : "ControlAltBrush";
                stroke = selected ? "AccentBrush" : "ControlStrongBrush";
                if (!enabled) { fill = selected ? "TextDisabledBrush" : "Transparent"; stroke = selected ? "Transparent" : "TextDisabledBrush"; }
            }
            else if (kind == "HomeTile")
            {
                // The tile stays a neutral card in both states; its accent icon chip carries "on".
                fill = pressed ? "SubtlePressedBrush" : hover ? "SubtleHoverBrush" : "Transparent";
                stroke = "Transparent";
                foreground = enabled ? "TextPrimaryBrush" : "TextDisabledBrush";
            }
            else if (kind == "HomeMore")
            {
                fill = pressed ? "SubtlePressedBrush" : hover ? "SubtleHoverBrush" : "Transparent";
                stroke = "DividerBrush";
                foreground = enabled ? "TextSecondaryBrush" : "TextDisabledBrush";
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
                if (thumb.RenderTransform is TranslateTransform transform)
                {
                    // A transform supplied by ControlTemplate can be frozen and
                    // shared. Direct BeginAnimation does not perform a
                    // Storyboard's automatic per-instance clone.
                    if (transform.IsFrozen) { transform = transform.CloneCurrentValue(); thumb.RenderTransform = transform; }
                    Number(transform, TranslateTransform.XProperty, x, animate);
                }
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
                if (Part("PART_PopupFrame") is { } popupFrame)
                {
                    var (viewportWidth, _) = PopupViewport(combo);
                    popupFrame.MaxWidth = Math.Min(360, Math.Max(0, viewportWidth - 16));
                    popupFrame.MinWidth = Math.Min(combo.ActualWidth + 8, popupFrame.MaxWidth);
                }
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
        private static (double Width, double Left) PopupViewport(ComboBox combo)
        {
            if (Window.GetWindow(combo) is not { } owner) return (SystemParameters.WorkArea.Width, 0);
            var viewport = owner.Content as FrameworkElement ?? owner;
            return (viewport.ActualWidth, combo.TranslatePoint(new Point(), viewport).X);
        }
        private CustomPopupPlacement[] PlacePopup(ComboBox combo)
        {
            var (viewportWidth, anchorLeft) = PopupViewport(combo);
            double left = Math.Max(8, Math.Min(anchorLeft - 4, viewportWidth - Math.Max(combo.ActualWidth + 8, 160) - 8));
            // Position the visible frame, excluding the transparent shadow room.
            var shadowRoom = Part("PART_PopupFrame")?.Margin ?? new Thickness(32);
            return [new CustomPopupPlacement(new Point(left - anchorLeft - shadowRoom.Left, -5 - shadowRoom.Top - Math.Min(Math.Max(0, combo.SelectedIndex), 8) * 34), PopupPrimaryAxis.Vertical)];
        }
        private void PopupOpened(object? sender, EventArgs e)
        {
            if (control is not ComboBox combo || Part("PART_PopupFrame") is not { } frame) return;
            ReleaseBackdrop(); backdrop = PopupBackdrop.Attach(frame, combo, "SelectBackdrop");
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
        private void PopupClosed(object? sender, EventArgs e) => ReleaseBackdrop();
        private void ReleaseBackdrop() { backdrop?.Dispose(); backdrop = null; }
        private static void PrepareMenuItems(ItemsControl owner)
        {
            var items = owner.Items.Cast<object>().Select((value, index) => (Value: value, Item: value as MenuItem ?? owner.ItemContainerGenerator.ContainerFromIndex(index) as MenuItem)).ToArray();
            bool leading = items.Any(entry => entry.Item is { } item && (item.Icon is not null || item.IsCheckable));
            for (int index = 0; index < items.Length; index++)
            {
                if (items[index].Item is not { } item) continue;
                bool caption = index == 0 && !item.IsEnabled && item.Icon is null && item.Header is string && index + 1 < items.Length && items[index + 1].Value is Separator;
                SetMenuLeading(item, leading); SetMenuCaption(item, caption); SetMenuText(item, item.Header is string);
                item.Margin = new Thickness(0, index > 0 && items[index - 1].Item is { } previous && !GetMenuCaption(previous) ? 2 : 0, 0, 0);
                if (item.GetValue(StateProperty) is State state) state.Update(false);
            }
        }
        private void MenuOpened(object sender, RoutedEventArgs e) { if (control is ContextMenu menu) OpenMenu(menu); }
        private void OpenMenu(ContextMenu menu)
        {
            if (menuOpen || Part("PART_PopupFrame") is not { } frame) return;
            menuOpen = true; PrepareMenuItems(menu);
            var owner = menu.PlacementTarget is FrameworkElement anchor ? PopupBackdrop.Owner(anchor) : null;
            if (owner?.Content is FrameworkElement viewport) { frame.MaxWidth = Math.Min(360, Math.Max(0, viewport.ActualWidth - 16)); frame.MinWidth = Math.Min(200, frame.MaxWidth); }
            // Shadow and entrance-animation room is outside the CSS menu box.
            menuX = menu.HorizontalOffset; menuY = menu.VerticalOffset;
            // Top/Left align the popup's far edge, so compensate with the far margin.
            double offsetX = menu.Placement switch
            {
                PlacementMode.Left => frame.Margin.Right - 4,
                PlacementMode.Right => -frame.Margin.Left + 4,
                _ => -frame.Margin.Left,
            };
            double offsetY = menu.Placement switch
            {
                PlacementMode.Top => frame.Margin.Bottom - 4,
                PlacementMode.Bottom => -frame.Margin.Top + 4,
                _ => -frame.Margin.Top,
            };
            menu.SetCurrentValue(ContextMenu.HorizontalOffsetProperty, menuX + offsetX);
            menu.SetCurrentValue(ContextMenu.VerticalOffsetProperty, menuY + offsetY);
            ReleaseBackdrop(); if (menu.PlacementTarget is FrameworkElement target) backdrop = PopupBackdrop.Attach(frame, target, "MenuBackdrop");
            UpdatePopupShadow(); AnimateMenu(frame, menu.Placement);
        }
        private void MenuClosed(object sender, RoutedEventArgs e) { if (control is ContextMenu menu) ResetMenu(menu); }
        private void ResetMenu(ContextMenu menu)
        {
            ReleaseBackdrop(); if (!menuOpen) return; menuOpen = false;
            menu.SetCurrentValue(ContextMenu.HorizontalOffsetProperty, menuX); menu.SetCurrentValue(ContextMenu.VerticalOffsetProperty, menuY);
        }
        private void SubmenuOpened(object? sender, EventArgs e)
        {
            if (control is not MenuItem item || Part("PART_PopupFrame") is not { } frame) return;
            PrepareMenuItems(item); ReleaseBackdrop(); backdrop = PopupBackdrop.Attach(frame, item, "SubmenuBackdrop"); UpdatePopupShadow(); AnimateMenu(frame, PlacementMode.Right);
        }
        private static void AnimateMenu(Border frame, PlacementMode placement)
        {
            frame.BeginAnimation(UIElement.OpacityProperty, null); frame.Opacity = 1;
            var move = new TranslateTransform(); frame.RenderTransform = move;
            if (!FluentMotion.Enabled) return;
            var axis = placement is PlacementMode.Right or PlacementMode.Left ? TranslateTransform.XProperty : TranslateTransform.YProperty;
            move.BeginAnimation(axis, new DoubleAnimation(placement == PlacementMode.Top ? 12 : -12, 0, FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
            frame.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FluentMotion.Normal) { EasingFunction = FluentMotion.Decelerate, FillBehavior = FillBehavior.Stop });
        }
    }
}
