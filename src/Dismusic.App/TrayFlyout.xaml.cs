using System.Windows;
using System.Windows.Input;
using Dismusic.App.ViewModels;

namespace Dismusic.App;

/// <summary>
/// Small panel shown when the tray icon is clicked: default output volume/mute and output switching.
/// Opens above the notification area and hides when it loses focus or Esc is pressed.
/// </summary>
public partial class TrayFlyout : Wpf.Ui.Controls.FluentWindow
{
    private const double ScreenMargin = 12;
    private readonly MainViewModel _viewModel;
    private readonly Action _openMain;
    private DateTime _hiddenAt = DateTime.MinValue;

    public TrayFlyout(MainViewModel viewModel, Action openMain)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _openMain = openMain;
        DataContext = viewModel;
        Loaded += (_, _) => Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Deactivated += (_, _) => HideFlyout();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideFlyout();
        };
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            HideFlyout();
            return;
        }
        // Clicking the tray icon while open first deactivates (hides) the flyout; don't reopen on that same click.
        if (DateTime.UtcNow - _hiddenAt < TimeSpan.FromMilliseconds(300)) return;

        _viewModel.RefreshNow();
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea; // excludes the taskbar
        Left = area.Right - ActualWidth - ScreenMargin;
        Top = area.Bottom - ActualHeight - ScreenMargin;
        Activate(); // keyboard users can Tab straight to the volume slider
    }

    private void HideFlyout()
    {
        if (!IsVisible) return;
        Hide();
        _hiddenAt = DateTime.UtcNow;
    }

    // Fires for mouse, keyboard (arrows/space) and screen readers alike. When the check comes from Windows'
    // state (binding), the device is already the default and this is a no-op.
    private void OnOutputChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceViewModel device } && !device.IsDefault)
            _viewModel.MakeDefaultOutput(device);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        HideFlyout();
        _openMain();
    }
}
