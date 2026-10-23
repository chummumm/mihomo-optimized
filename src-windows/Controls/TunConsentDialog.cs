using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Threading;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal static class TunConsentDialog
{
    private static readonly HashSet<Window> pending = [];
    internal static async Task<bool> Run(Window owner, Func<Task> operation, Action<string> reportError, bool enableTun = true)
    {
        if (!pending.Add(owner)) return false;
        try { await Dispatcher.Yield(DispatcherPriority.Background); return Show(owner, operation, reportError, enableTun); }
        finally { pending.Remove(owner); }
    }
    private static bool Show(Window owner, Func<Task> operation, Action<string> reportError, bool enableTun)
    {
        var description = Ui.Label("TUN 模式通过一个受限的 Windows 服务接管系统流量。安装时需要一次管理员授权，之后开启或关闭 TUN 都不再需要授权。");
        description.Margin = new Thickness(0, 0, 0, 12);
        var hint = Ui.Hint("服务只接受本应用的请求，卸载应用时会一并移除。仅安装版支持。", 13); hint.LineHeight = 20;
        string label = enableTun ? "安装并开启 TUN" : "安装服务";
        var install = new Button { Content = FluentControls.ButtonContent("shield-check", label) }.Id("InstallTunConfirmed");
        FluentControls.Primary(install); L.Bind(install, AutomationProperties.NameProperty, label);
        var cancel = new Button { Content = Ui.Label("取消") }.Id("CancelTunInstall");
        var actions = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.Children.Add(install); Grid.SetColumn(cancel, 2); actions.Children.Add(cancel);
        bool busy = false;
        var dialog = new FluentDialog(owner, "安装 TUN 系统服务？", Ui.Column(description, hint, actions), []).Id("TunConsentDialog");
        dialog.CanDismiss = () => !busy;
        install.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; install.IsEnabled = cancel.IsEnabled = false;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var progress = new FluentProgressRing { Width = 16, Height = 16, IsActive = true, IsButtonSpinner = true, Margin = new Thickness(0, 0, 8, 0) }; progress.SetResourceReference(FluentProgressRing.ForegroundProperty, "TextSecondaryBrush");
            var text = Ui.Label(label); text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); content.Children.Add(progress); content.Children.Add(text); install.Content = content;
            try { await operation(); busy = false; dialog.Complete(true); }
            catch (Exception exception) { reportError(exception.Message); }
            finally { busy = false; install.IsEnabled = cancel.IsEnabled = true; install.Content = FluentControls.ButtonContent("shield-check", label); }
        };
        cancel.Click += (_, _) => dialog.Complete(false);
        dialog.Loaded += (_, _) => cancel.Focus(); return dialog.ShowDialog() == true;
    }
}
