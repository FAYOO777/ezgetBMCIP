using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EzGetBmcIp;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();

        // 运行时监听系统主题变化，自动切换
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica);

        _vm = new MainViewModel();
        _vm.RequestClose += OnRequestClose;
        _vm.OpenBrowserRequested += OnOpenBrowser;
        DataContext = _vm;

        Closing += OnWindowClosing;
    }

    private void GitHubLink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void OnRequestClose()
    {
        if (_allowClose)
        {
            Application.Current.Shutdown();
            return;
        }

        _allowClose = true;
        _vm.CancelFlow();
        await _vm.CleanupAsync();
        Application.Current.Shutdown();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        _allowClose = true;
        _vm.CancelFlow();
        await _vm.CleanupAsync();
        Application.Current.Shutdown();
    }

    private static void OnOpenBrowser(string ipAddress)
    {
        Process.Start(new ProcessStartInfo("http://" + ipAddress)
        {
            UseShellExecute = true
        });
    }
}
