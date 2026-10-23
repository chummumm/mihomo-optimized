using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

internal sealed class TunConsentDialog : Window
{
    private TunConsentDialog(Window owner)
    {
        Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; SizeToContent = SizeToContent.Height;
        Width = Math.Min(500, Math.Max(320, owner.ActualWidth - 40)); ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        L.Bind(this, TitleProperty, "安装 TUN 系统服务？"); this.Id("TunConsentDialog");
        SetResourceReference(BackgroundProperty, "SurfaceBrush"); SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        var body = Ui.Column(Ui.Label("安装 TUN 系统服务？", 20, true), Ui.Label("TUN 模式通过一个受限的 Windows 服务接管系统流量。安装时需要一次管理员授权，之后开启或关闭 TUN 都不再需要授权。"), Ui.Hint("服务只接受本应用的请求，卸载应用时会一并移除。仅安装版支持。"));
        foreach (FrameworkElement child in body.Children) child.Margin = new Thickness(0, 0, 0, 16);
        var buttons = new Grid(); buttons.ColumnDefinitions.Add(new ColumnDefinition()); buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var install = new Button { Content = FluentControls.ButtonContent("shield-check", "安装并开启 TUN"), Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch }.Id("InstallTunConfirmed");
        FluentControls.Primary(install); L.Bind(install, AutomationProperties.NameProperty, "安装并开启 TUN"); install.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = Ui.Label("取消"), MinWidth = 88, IsCancel = true, Style = FluentControls.Style("Button") }; cancel.Click += (_, _) => DialogResult = false; Grid.SetColumn(cancel, 1);
        buttons.Children.Add(install); buttons.Children.Add(cancel); body.Children.Add(buttons); body.Margin = new Thickness(24); Content = body;
        Loaded += (_, _) => cancel.Focus();
    }
    internal static bool Confirm(Window owner) => new TunConsentDialog(owner).ShowDialog() == true;
}
