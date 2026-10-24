using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Mihomo.Desktop.Services;

namespace Mihomo.Desktop.Controls;

/// <summary>The original desktop's dismissible, version-scoped update InfoBar.</summary>
internal sealed class DesktopUpdateNotice : Border
{
    private readonly AppSession session;
    private readonly TextBlock version = Ui.Text("");
    private readonly TextBlock title = Ui.Label("发现新版本", 14, true);

    internal DesktopUpdateNotice(AppSession session)
    {
        this.session = session;
        this.Id("DesktopUpdateNotice");
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        CornerRadius = new CornerRadius(4); BorderThickness = new Thickness(1);
        Padding = new Thickness(16, 8, 8, 8); MinHeight = 48;
        Visibility = Visibility.Collapsed;
        SetResourceReference(BackgroundProperty, "InfoBackgroundBrush");
        SetResourceReference(BorderBrushProperty, "CardStrokeBrush");
        var icon = new FluentIcon("info", 16) { Filled = true };
        icon.SetResourceReference(Control.ForegroundProperty, "AccentLinkBrush");
        var text = new WrapPanel { Orientation = Orientation.Horizontal };
        title.Margin = new Thickness(0, 0, 12, 0); text.Children.Add(title); text.Children.Add(version);
        var view = new Button { Height = 28, FontSize = 13, Padding = new Thickness(10, 0, 10, 0), Content = Ui.Label("查看更新", 13) }.Id("ViewAppUpdate");
        L.Bind(view, AutomationProperties.NameProperty, "查看更新");
        view.Click += (_, _) => { session.DismissUpdate(); session.Navigate(new NavigationTarget("settings", "desktop-app-update")); };
        var dismiss = FluentControls.IconButton("dismiss", "关闭更新提示", "DismissAppUpdate", 28);
        dismiss.Click += (_, _) => session.DismissUpdate();
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        view.Margin = new Thickness(0, 0, 4, 0); actions.Children.Add(view); actions.Children.Add(dismiss);
        var layout = new NoticeLayout(); layout.Children.Add(icon); layout.Children.Add(text); layout.Children.Add(actions); Child = layout;
    }

    internal bool Available => session.Update.Bool("available") && session.Update.Text("version") is { Length: > 0 } value && value != session.UpdateDismissedVersion;

    internal void Refresh()
    {
        bool entering = Visibility != Visibility.Visible && Available;
        version.Text = session.Update.Text("version");
        Visibility = Available ? Visibility.Visible : Visibility.Collapsed;
        if (entering) FluentMotion.Enter(this);
    }

    internal void SetContainerWidth(double width, bool narrowWindow)
    {
        double gutter = narrowWindow ? 0 : 15;
        double container = Math.Max(0, width - gutter);
        double inset = container <= 640 ? 16 : Math.Clamp(container * .04, 16, 40);
        Margin = new Thickness(inset, 12, inset + gutter, 0);
    }

    private sealed class NoticeLayout : Panel
    {
        private bool wrapped;
        private double firstRow;
        protected override Size MeasureOverride(Size available)
        {
            double width = double.IsInfinity(available.Width) ? 1000 : available.Width;
            Children[0].Measure(new Size(16, double.PositiveInfinity));
            Children[2].Measure(new Size(width, double.PositiveInfinity));
            Children[1].Measure(new Size(Math.Max(0, width - 28), double.PositiveInfinity));
            wrapped = 28 + Children[1].DesiredSize.Width + 12 + Children[2].DesiredSize.Width > width;
            if (!wrapped) Children[1].Measure(new Size(Math.Max(0, width - 40 - Children[2].DesiredSize.Width), double.PositiveInfinity));
            firstRow = Math.Max(16, Children[1].DesiredSize.Height);
            return new Size(width, wrapped ? firstRow + 8 + Children[2].DesiredSize.Height : Math.Max(firstRow, Children[2].DesiredSize.Height));
        }
        protected override Size ArrangeOverride(Size final)
        {
            double height = wrapped ? firstRow : final.Height;
            Children[0].Arrange(new Rect(0, Math.Max(0, (height - 16) / 2), 16, 16));
            double textWidth = Math.Max(0, final.Width - 28 - (wrapped ? 0 : 12 + Children[2].DesiredSize.Width));
            Children[1].Arrange(new Rect(28, Math.Max(0, (height - Children[1].DesiredSize.Height) / 2), textWidth, Children[1].DesiredSize.Height));
            Children[2].Arrange(new Rect(Math.Max(0, final.Width - Children[2].DesiredSize.Width), wrapped ? firstRow + 8 : Math.Max(0, (height - Children[2].DesiredSize.Height) / 2), Children[2].DesiredSize.Width, Children[2].DesiredSize.Height));
            return final;
        }
    }
}
