namespace Mihomo.Desktop.Controls;

public interface IPageBackNavigation
{
    bool CanGoBack { get; }
    Task<bool> GoBackAsync();
    event Action? BackStateChanged;
}
