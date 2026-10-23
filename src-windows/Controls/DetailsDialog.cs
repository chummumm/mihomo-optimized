using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal static class DetailsDialog
{
    private static readonly HashSet<Window> pending = [];

    public static async void Show(FrameworkElement owner, string title, string content)
    {
        if (Window.GetWindow(owner) is not { } window || !pending.Add(window)) return;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!window.IsLoaded) return;
            var text = new TextBox
            {
                Text = content, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontSize = 13, Padding = new Thickness(8), MinHeight = 160, MaxHeight = Math.Max(160, window.ActualHeight - 240)
            }.Id("DetailsContent");
            text.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
            var copy = new Button { Content = Ui.Label("复制") };
            var close = new Button { Content = Ui.Label("关闭") };
            var dialog = new FluentDialog(window, title, text, [copy, close], large: true).Id("DetailsDialog");
            copy.Click += (_, _) => Ui.Copy(content);
            close.Click += (_, _) => dialog.Complete(false);
            dialog.Loaded += (_, _) => close.Focus();
            dialog.ShowDialog();
        }
        finally { pending.Remove(window); }
    }
}
