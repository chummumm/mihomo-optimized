using System.Windows;
using System.Windows.Controls;

namespace Mihomo.Desktop.Controls;

internal static class DetailsDialog
{
    public static void Show(FrameworkElement owner, string title, string content)
    {
        var dialog = new Window { Title = title, Owner = Window.GetWindow(owner), Width = 720, Height = 520, MinWidth = 420, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(Control.BackgroundProperty, "AppBackgroundBrush");
        var layout = new DockPanel { Margin = new Thickness(20) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "复制", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Ui.Copy(content);
        var close = new Button { Content = "关闭", IsCancel = true, Padding = new Thickness(18, 6, 18, 6) };
        actions.Children.Add(copy); actions.Children.Add(close); DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        layout.Children.Add(new TextBox { Text = content, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 13, Padding = new Thickness(12) });
        dialog.Content = layout; dialog.ShowDialog();
    }
}
