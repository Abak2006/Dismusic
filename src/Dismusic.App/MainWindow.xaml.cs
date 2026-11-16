using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Dismusic.App.ViewModels;
using Dismusic.App.Views;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Dismusic.App;

public partial class MainWindow : FluentWindow
{
    private static readonly Type[] PageOrder =
        [typeof(AppsPage), typeof(MultiOutputPage), typeof(EnhancePage), typeof(ScreensPage), typeof(SettingsPage)];

    public MainWindow(MainViewModel viewModel, ImageSource icon)
    {
        InitializeComponent();
        DataContext = viewModel;
        Icon = icon;
        AppTitleBar.Icon = new ImageIcon { Source = icon, Width = 16, Height = 16 };
        RootNavigation.SetPageProviderService(new PageProvider(viewModel));

        // Keyboard: Ctrl+1..5 switch pages, F5 refreshes, Ctrl+W hides to the notification area.
        for (int i = 0; i < PageOrder.Length; i++)
        {
            var page = PageOrder[i];
            InputBindings.Add(new KeyBinding(new RelayCommand(() => RootNavigation.Navigate(page)), Key.D1 + i, ModifierKeys.Control));
        }
        InputBindings.Add(new KeyBinding(new RelayCommand(viewModel.RefreshNow), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(Close), Key.W, ModifierKeys.Control));

        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this);
            RootNavigation.Navigate(typeof(AppsPage));
        };
    }

    /// <summary>Creates each page once and gives it the right view model.</summary>
    private sealed class PageProvider(MainViewModel viewModel) : INavigationViewPageProvider
    {
        private readonly Dictionary<Type, FrameworkElement> _pages = new();

        public object? GetPage(Type pageType)
        {
            if (_pages.TryGetValue(pageType, out var page)) return page;
            page = (FrameworkElement)Activator.CreateInstance(pageType)!;
            page.DataContext = pageType == typeof(EnhancePage) ? viewModel.Enhance : viewModel;
            _pages[pageType] = page;
            return page;
        }
    }
}
