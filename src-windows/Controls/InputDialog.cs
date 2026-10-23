using System.Windows;
using System.Windows.Controls;

namespace Mihomo.Desktop.Controls;

internal static class InputDialog
{
    public static string? Show(FrameworkElement owner, string title, string label, bool multiline = false)
    {
        var dialog = new Window { Title = title, Owner = Window.GetWindow(owner), Width = 620, Height = multiline ? 440 : 230, MinWidth = 420, MinHeight = 200, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = multiline ? ResizeMode.CanResize : ResizeMode.NoResize };
        dialog.SetResourceReference(Control.BackgroundProperty, "AppBackgroundBrush");
        var layout = new DockPanel { Margin = new Thickness(24) };
        var text = Ui.Text(label); text.Margin = new Thickness(0, 0, 0, 12); DockPanel.SetDock(text, Dock.Top); layout.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var accept = new Button { Content = "导入", IsDefault = true, Padding = new Thickness(20, 6, 20, 6), Margin = new Thickness(0, 0, 8, 0), IsEnabled = false };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(20, 6, 20, 6) };
        actions.Children.Add(accept); actions.Children.Add(cancel); DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        var input = new TextBox { AcceptsReturn = multiline, AcceptsTab = multiline, TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, Padding = new Thickness(8), MaxLength = multiline ? 4 * 1024 * 1024 : 8192 };
        input.TextChanged += (_, _) => accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        accept.Click += (_, _) => dialog.DialogResult = true;
        layout.Children.Add(input); dialog.Content = layout; dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
