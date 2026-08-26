using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;

namespace EzGetBmcIp.Legacy
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;
        private bool _allowClose;
        private bool _isClosing;
        private bool _isCollectingSupportBundle;
        private int _supportFeedbackGeneration;
        private int _lastSupportProgress;

        public MainWindow()
        {
            InitializeComponent();

            _vm = new MainViewModel();
            _vm.RequestClose += OnRequestClose;
            _vm.OpenBrowserRequested += OnOpenBrowser;
            _vm.ConsentRequested += OnConsentRequested;
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

        private static void OnOpenBrowser(string url)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }

        private bool OnConsentRequested(ConsentNotice notice)
        {
            return ConsentDialog.ShowFor(this, notice);
        }

        private void OpenBmcButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_vm.DiscoveredIpUrl))
                OnOpenBrowser(_vm.DiscoveredIpUrl);
        }

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!SupportBundleShortcut.Matches(Keyboard.Modifiers, e.Key, e.SystemKey))
                return;

            e.Handled = true;
            if (_isCollectingSupportBundle)
                return;

            _isCollectingSupportBundle = true;
            var feedbackGeneration = ++_supportFeedbackGeneration;
            ShowSupportProgress(new SupportBundleProgress(0, "正在准备日志和诊断..."));
            var progress = new SupportProgressReporter(this);
            try
            {
                App.LogSupport("Legacy support bundle collection requested.");
                var archivePath = await SupportBundleCollector.CreateAsync(
                    "ezgetBMCIP-legacy-support",
                    App.LogFilePath,
                    reportPath => LegacyDiagnosticReporter.WriteReportAsync(_vm, reportPath, progress),
                    progress);
                ShowSupportCompletion("支持包已生成，正在打开文件夹...");
                OpenSupportFolder(archivePath);
                App.LogSupport("Legacy support bundle written: " + archivePath);
                ShowSupportCompletion("已完成，已打开支持包文件夹。");
                var _ = HideSupportFeedbackAfterAsync(feedbackGeneration, TimeSpan.FromSeconds(3));
            }
            catch (Exception ex)
            {
                App.LogSupport("Legacy support bundle collection failed: " + ex);
                ShowSupportFailure("无法收集日志和诊断：" + ex.Message);
                MessageBox.Show(
                    "无法收集日志和诊断：" + ex.Message,
                    "IPMI/BMC 直连助手", MessageBoxButton.OK, MessageBoxImage.Warning);
                var _ = HideSupportFeedbackAfterAsync(feedbackGeneration, TimeSpan.FromSeconds(5));
            }
            finally
            {
                _isCollectingSupportBundle = false;
            }
        }

        private void ShowSupportProgress(SupportBundleProgress progress)
        {
            _lastSupportProgress = progress.Percent;
            SupportProgressCard.Visibility = Visibility.Visible;
            SupportProgressTitle.Text = "正在收集日志和诊断";
            SupportProgressTitle.Foreground = Brushes.DodgerBlue;
            SupportProgressDetail.Text = progress.Message;
            SupportProgressPercent.Text = progress.Percent + "%";
            SupportProgressBar.Value = progress.Percent;
            SupportProgressBar.Foreground = Brushes.DodgerBlue;
        }

        private void ShowSupportCompletion(string detail)
        {
            _lastSupportProgress = 100;
            SupportProgressCard.Visibility = Visibility.Visible;
            SupportProgressTitle.Text = "日志和诊断已收集";
            SupportProgressTitle.Foreground = Brushes.ForestGreen;
            SupportProgressDetail.Text = detail;
            SupportProgressPercent.Text = "100%";
            SupportProgressBar.Value = 100;
            SupportProgressBar.Foreground = Brushes.ForestGreen;
        }

        private void ShowSupportFailure(string detail)
        {
            _lastSupportProgress = Math.Min(_lastSupportProgress, 95);
            SupportProgressCard.Visibility = Visibility.Visible;
            SupportProgressTitle.Text = "日志和诊断收集失败";
            SupportProgressTitle.Foreground = Brushes.Firebrick;
            SupportProgressDetail.Text = detail;
            SupportProgressPercent.Text = _lastSupportProgress + "%";
            SupportProgressBar.Value = _lastSupportProgress;
            SupportProgressBar.Foreground = Brushes.Firebrick;
        }

        private async Task HideSupportFeedbackAfterAsync(int feedbackGeneration, TimeSpan delay)
        {
            await Task.Delay(delay);
            if (feedbackGeneration != _supportFeedbackGeneration || _isCollectingSupportBundle)
                return;

            SupportProgressCard.Visibility = Visibility.Collapsed;
        }

        private sealed class SupportProgressReporter : IProgress<SupportBundleProgress>
        {
            private readonly MainWindow _window;

            public SupportProgressReporter(MainWindow window)
            {
                _window = window;
            }

            public void Report(SupportBundleProgress value)
            {
                if (_window.Dispatcher.CheckAccess())
                {
                    _window.ShowSupportProgress(value);
                    return;
                }

                _window.Dispatcher.BeginInvoke(new Action(() => _window.ShowSupportProgress(value)));
            }
        }

        private static void OpenSupportFolder(string archivePath)
        {
            var directory = System.IO.Path.GetDirectoryName(archivePath);
            if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
                throw new InvalidOperationException("支持包目录不可用。");

            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + directory + "\"")
            {
                UseShellExecute = false
            });
        }
    }
}
