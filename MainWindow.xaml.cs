using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace EzGetBmcIp;

public partial class MainWindow : FluentWindow
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
        ApplyTestReleaseTitle();

        // 运行时监听系统主题变化，自动切换
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica);

        _vm = new MainViewModel();
        _vm.RequestClose += OnRequestClose;
        _vm.OpenBrowserRequested += OnOpenBrowser;
        _vm.ConsentRequested += OnConsentRequested;
        DataContext = _vm;

        Closing += OnWindowClosing;
    }

    private void ApplyTestReleaseTitle()
    {
        var version = GetType().Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(version) || !version.Contains('-'))
            return;

        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
            version = version[..plusIndex];

        Title += "（" + version + " 测试版）";
    }

    private void BmcUrl_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
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

    private static void OnOpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    private bool OnConsentRequested(ConsentNotice notice)
    {
        return ConsentDialog.ShowFor(this, notice);
    }

    private void OctetTextBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        // Only allow digits
        e.Handled = !int.TryParse(e.Text, out _);
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
            AppLogger.Log("Support bundle collection requested.");
            var archivePath = await SupportBundleCollector.CreateAsync(
                "ezgetBMCIP-support",
                AppLogger.LogFilePath,
                reportPath => DiagnosticReporter.WriteReportAsync(_vm, reportPath, progress),
                progress);
            ShowSupportCompletion("支持包已生成，正在打开文件夹...");
            OpenSupportFolder(archivePath);
            AppLogger.Log("Support bundle written: " + archivePath);
            ShowSupportCompletion("已完成，已打开支持包文件夹。");
            _ = HideSupportFeedbackAfterAsync(feedbackGeneration, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            AppLogger.Log("Support bundle collection failed: " + ex);
            ShowSupportFailure("无法收集日志和诊断：" + ex.Message);
            System.Windows.MessageBox.Show("无法收集日志和诊断：" + ex.Message,
                "IPMI/BMC 直连助手", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            _ = HideSupportFeedbackAfterAsync(feedbackGeneration, TimeSpan.FromSeconds(5));
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
        SupportProgressTitle.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        SupportProgressDetail.Text = progress.Message;
        SupportProgressPercent.Text = progress.Percent + "%";
        SupportProgressBar.Value = progress.Percent;
        SupportProgressBar.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
    }

    private void ShowSupportCompletion(string detail)
    {
        _lastSupportProgress = 100;
        SupportProgressCard.Visibility = Visibility.Visible;
        SupportProgressTitle.Text = "日志和诊断已收集";
        SupportProgressTitle.Foreground = System.Windows.Media.Brushes.ForestGreen;
        SupportProgressDetail.Text = detail;
        SupportProgressPercent.Text = "100%";
        SupportProgressBar.Value = 100;
        SupportProgressBar.Foreground = System.Windows.Media.Brushes.ForestGreen;
    }

    private void ShowSupportFailure(string detail)
    {
        _lastSupportProgress = Math.Min(_lastSupportProgress, 95);
        SupportProgressCard.Visibility = Visibility.Visible;
        SupportProgressTitle.Text = "日志和诊断收集失败";
        SupportProgressTitle.Foreground = System.Windows.Media.Brushes.Firebrick;
        SupportProgressDetail.Text = detail;
        SupportProgressPercent.Text = _lastSupportProgress + "%";
        SupportProgressBar.Value = _lastSupportProgress;
        SupportProgressBar.Foreground = System.Windows.Media.Brushes.Firebrick;
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
