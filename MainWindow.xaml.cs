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
    private bool _isClosing;

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
        await CleanupAndShutdownAsync();
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        await CleanupAndShutdownAsync();
    }

    private async Task CleanupAndShutdownAsync()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        _vm.CancelFlow();
        var cleanupSucceeded = await _vm.CleanupAsync();
        if (!cleanupSucceeded)
        {
            _isClosing = false;
            return;
        }

        _allowClose = true;
        Application.Current.Shutdown();
    }

    private static void OnOpenBrowser(string ipAddress)
    {
        Process.Start(new ProcessStartInfo("http://" + ipAddress)
        {
            UseShellExecute = true
        });
    }

    private void OctetTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        // Only allow digits
        e.Handled = !int.TryParse(e.Text, out _);
    }

    private void OpenBmcButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_vm.DiscoveredIp))
            OnOpenBrowser(_vm.DiscoveredIp);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        OpenExplorerAt(AppLogger.LogFilePath);
    }

    private static void OpenExplorerAt(string filePath)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var arg = "/select,\"" + filePath + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arg)
            {
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            AppLogger.Log("Failed to open explorer: " + ex.Message);
            System.Windows.MessageBox.Show("\u65e0\u6cd5\u6253\u5f00\u65e5\u5fd7\u6587\u4ef6\uff1a" + ex.Message,
                "ezgetBMCIP", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }
}
