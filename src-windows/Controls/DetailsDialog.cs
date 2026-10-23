using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal static class DetailsDialog
{
    public static void Show(FrameworkElement owner, string title, string content) => ShowLive(owner, title, () => content);
    public static void ShowLive(FrameworkElement owner, string title, Func<string> content, Func<Task>? closeConnection = null, Func<bool>? canClose = null)
    {
        var dialog = new Window { Owner = Window.GetWindow(owner), Width = 720, Height = 560, MinWidth = 420, MinHeight = 280, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        L.Bind(dialog, Window.TitleProperty, title); dialog.SetResourceReference(Control.BackgroundProperty, "AppBackgroundBrush"); dialog.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        var layout = new DockPanel { Margin = new Thickness(20) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var text = new TextBox { Text = content(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 13, Padding = new Thickness(12) }.Id("DetailsContent");
        Button? terminate = null;
        if (closeConnection is not null)
        {
            terminate = L.Bind(new Button { Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) }.Id("CloseDetailedConnection"), ContentControl.ContentProperty, "关闭此连接");
            terminate.Click += async (_, _) => { terminate.IsEnabled = false; try { await closeConnection(); } catch (Exception error) { MessageBox.Show(dialog, error.Message, L.T("关闭连接"), MessageBoxButton.OK, MessageBoxImage.Error); } finally { terminate.IsEnabled = canClose?.Invoke() ?? true; } };
            actions.Children.Add(terminate);
        }
        var copy = L.Bind(new Button { Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) }, ContentControl.ContentProperty, "复制"); copy.Click += (_, _) => Ui.Copy(text.Text);
        var close = L.Bind(new Button { IsCancel = true, Padding = new Thickness(18, 6, 18, 6) }, ContentControl.ContentProperty, "关闭");
        actions.Children.Add(copy); actions.Children.Add(close); DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions); layout.Children.Add(text);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => { string value = content(); if (value != text.Text) { var offset = text.VerticalOffset; var selection = text.SelectionStart; text.Text = value; text.Select(Math.Min(selection, value.Length), 0); text.ScrollToVerticalOffset(offset); } if (terminate is not null) terminate.IsEnabled = canClose?.Invoke() ?? true; };
        dialog.Loaded += (_, _) => timer.Start(); dialog.Closed += (_, _) => timer.Stop(); dialog.Content = layout; dialog.ShowDialog();
    }
}
