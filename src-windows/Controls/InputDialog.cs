using System.Windows;
using System.Windows.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal static class InputDialog
{
    public static string? Show(FrameworkElement owner, string title, string label, bool multiline = false)
    {
        var input = new TextBox { AcceptsReturn = multiline, AcceptsTab = multiline, TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.Wrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, MaxLength = multiline ? 4 * 1024 * 1024 : 8192 };
        if (multiline) { input.Height = 260; input.Padding = new Thickness(11, 8, 11, 8); input.SetResourceReference(Control.FontFamilyProperty, "MonoFont"); input.FontSize = 12; }
        var hint = Ui.Text(label); hint.Margin = new Thickness(0, 0, 0, 12);
        var accept = new Button { Content = Ui.Label("导入"), IsDefault = true, IsEnabled = false }; FluentControls.Primary(accept);
        var cancel = new Button { Content = Ui.Label("取消") };
        var dialog = new FluentDialog(Window.GetWindow(owner), title, Ui.Column(hint, input), [accept, cancel], multiline);
        input.TextChanged += (_, _) => accept.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        accept.Click += (_, _) => dialog.Complete(true); cancel.Click += (_, _) => dialog.Complete(false);
        dialog.Loaded += (_, _) => input.Focus();
        return dialog.ShowDialog() == true ? input.Text : null;
    }
}
