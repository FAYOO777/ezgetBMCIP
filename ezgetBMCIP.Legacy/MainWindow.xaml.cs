using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace EzGetBmcIp.Legacy
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private bool _allowClose;
        private bool _isClosing;

        public MainWindow()
        {
            InitializeComponent();

            _vm = new MainViewModel();
            _vm.RequestClose += OnRequestClose;
            _vm.OpenBrowserRequested += OnOpenBrowser;
            DataContext = _vm;

            Closing += OnWindowClosing;
        }

        private async void OnRequestClose()
        {
            if (_allowClose)
            {
                Application.Current.Shutdown();
                return;
            }
            if (_isClosing) return;

            _isClosing = true;
            if (await _vm.CleanupAsync())
            {
                _allowClose = true;
                Application.Current.Shutdown();
            }
            else
            {
                _isClosing = false;
            }
        }

        private async void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_allowClose)
                return;

            e.Cancel = true;
            if (_isClosing) return;

            _isClosing = true;
            if (await _vm.CleanupAsync())
            {
                _allowClose = true;
                Application.Current.Shutdown();
            }
            else
            {
                _isClosing = false;
            }
        }

        private static void OnOpenBrowser(string ipAddress)
        {
            Process.Start(new ProcessStartInfo("http://" + ipAddress) { UseShellExecute = true });
        }

        private void OpenBmcButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_vm.DiscoveredIp))
                OnOpenBrowser(_vm.DiscoveredIp);
        }

        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var logPath = App.LogFilePath;
                var dir = System.IO.Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                var arg = "/select,\"" + logPath + "\"";
                Process.Start(new ProcessStartInfo("explorer.exe", arg)
                {
                    UseShellExecute = false
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "\u65e0\u6cd5\u6253\u5f00\u65e5\u5fd7\u6587\u4ef6\uff1a" + ex.Message,
                    "ezgetBMCIP", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
