using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace EzGetBmcIp.Legacy
{
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly SubnetConfig _subnetConfig = new SubnetConfig();
        private DhcpServer _dhcpServer;
        private CancellationTokenSource _flowCts;
        private WiredAdapter _selectedAdapter;
        private AdapterOriginalConfig _originalConfig;
        private bool _isCleaningUp;
        private string _dhcpServerError;

        private Task _flowTask = Task.CompletedTask;
        private bool _isClosing;

        private bool _isFlowStarted;

        public SubnetConfig SubnetConfig => _subnetConfig;

        public ObservableCollection<WiredAdapter> Adapters { get; } = new ObservableCollection<WiredAdapter>();
        private WiredAdapter _selectedAdapterItem;
        public WiredAdapter SelectedAdapterItem { get => _selectedAdapterItem; set { _selectedAdapterItem = value; OnPropertyChanged(); } }

        private string _statusText = "\u9009\u62e9\u7f51\u5361\u5e76\u5f00\u59cb";
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }

        private string _detailText = "\u9009\u62e9\u7f51\u5361\uff0c\u914d\u7f6e\u7f51\u6bb5\uff0c\u81ea\u52a8\u83b7\u53d6 BMC \u5730\u5740\u3002";
        public string DetailText { get => _detailText; set { _detailText = value; OnPropertyChanged(); } }

        private string _activityText = "";
        public string ActivityText { get => _activityText; set { _activityText = value; OnPropertyChanged(); } }

        private string _badgeText = "\u7b49\u5f85\u4e2d";
        public string BadgeText { get => _badgeText; set { _badgeText = value; OnPropertyChanged(); } }

        private string _badgeColor = "#666";
        public string BadgeColor { get => _badgeColor; set { _badgeColor = value; OnPropertyChanged(); } }

        private bool _adapterSelectionEnabled = true;
        public bool AdapterSelectionEnabled { get => _adapterSelectionEnabled; set { _adapterSelectionEnabled = value; OnPropertyChanged(); } }

        private bool _startButtonEnabled = true;
        public bool StartButtonEnabled { get => _startButtonEnabled; set { _startButtonEnabled = value; OnPropertyChanged(); } }

        private bool _isCleanupDone;
        private string _discoveredIp;
        public string DiscoveredIp { get => _discoveredIp; set { _discoveredIp = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowIpResult)); } }

        public bool ShowAdapterSelection => !_isFlowStarted;
        public bool ShowRunning => _isFlowStarted && !_isCleanupDone;
        public bool ShowIpResult => !string.IsNullOrEmpty(_discoveredIp) && _isFlowStarted;

        public string AdapterCardLine => _selectedAdapter?.DisplayName ?? "";

        public ICommand StartCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand CancelCommand { get; }

        public event Action RequestClose;
        public event Action<string> OpenBrowserRequested;

        public MainViewModel()
        {
            StartCommand = new RelayCommand(async _ => await StartFlowAsync());
            ExitCommand = new RelayCommand(_ => RequestClose?.Invoke());
            CancelCommand = new RelayCommand(_ => CancelFlow());
            var _ = InitializeAsync();
        }

        private static void Log(string message)
        {
            NetworkConfigManager.Logger?.Invoke("[Legacy] " + message);
        }

        private async Task InitializeAsync()
        {
            try
            {
                Log("Adapter enumeration started");
                var adapters = await Task.Run(() => NetworkConfigManager.GetWiredAdapters());
                Log("Adapter enumeration done: " + adapters.Count + " adapter(s) found");
                if (adapters.Count == 0)
                    throw new InvalidOperationException("\u672a\u68c0\u6d4b\u5230\u53ef\u7528\u7f51\u5361");
                foreach (var a in adapters)
                {
                    Adapters.Add(a);
                    Log("Adapter: " + a.Name + " | " + a.Description + " | id=" + a.Id + " | mac=" + a.MacAddress);
                }
                SelectedAdapterItem = Adapters[0];
                Log("Selected adapter: " + Adapters[0].Name);
                Log("Initialize: " + adapters.Count + " adapter(s), selected: " + Adapters[0].Name);
            }
            catch (Exception ex)
            {
                Log("Initialize failed: " + ex.Message);
                DetailText = ex.Message;
                StartButtonEnabled = false;
            }
        }

        private Task StartFlowAsync()
        {
            if (SelectedAdapterItem == null) return Task.CompletedTask;
            if (!_subnetConfig.IsPrivateSubnet)
            {
                var message = _subnetConfig.ValidationError ?? "\u81ea\u5b9a\u4e49\u7f51\u6bb5\u65e0\u6548\u3002";
                Log("Flow blocked: invalid subnet " + _subnetConfig.ServerDisplay + " - " + message);
                StatusText = "\u7f51\u6bb5\u4e0d\u53ef\u7528";
                DetailText = message + " \u516c\u7f51\u5730\u5740\u53ef\u80fd\u88ab\u7cfb\u7edf\u4ee3\u7406\u6216\u8def\u7531\u7b56\u7565\u62e6\u622a\uff0c\u76f4\u8fde\u573a\u666f\u8bf7\u4f7f\u7528\u79c1\u6709\u7f51\u6bb5\u3002";
                BadgeText = "\u7f51\u6bb5\u9519\u8bef";
                BadgeColor = "#D13438";
                return Task.CompletedTask;
            }
            _selectedAdapter = SelectedAdapterItem;
            _adapterSelectionEnabled = false;
            _startButtonEnabled = false;
            _isFlowStarted = true;
            OnPropertyChanged(nameof(ShowAdapterSelection));
            OnPropertyChanged(nameof(ShowRunning));
            OnPropertyChanged(nameof(AdapterCardLine));
            _flowCts = new CancellationTokenSource();
            _dhcpServerError = null;
            _flowTask = RunFlowAsync(_flowCts.Token);
            return Task.CompletedTask;
        }

        private async Task RunFlowAsync(CancellationToken ct)
        {
            Log("Flow started, adapter: " + (_selectedAdapter?.Name ?? "null") + ", subnet: " + _subnetConfig.ServerDisplay);
            try
            {
                await ConfigureAdapterAsync(ct);
                await WaitForLinkAsync(ct);
                var lease = await WaitForLeaseAsync(ct);
                _discoveredIp = lease.IpAddress.ToString();
                Log("Flow success, BMC IP: " + _discoveredIp);
                OnPropertyChanged(nameof(DiscoveredIp));
                StatusText = "BMC \u7ba1\u7406\u9875\u9762\u5df2\u6253\u5f00";
                DetailText = "\u5730\u5740: http://" + lease.IpAddress;
                BadgeText = "\u5df2\u5b8c\u6210";
                BadgeColor = "#107C10";
                ActivityText = "BMC \u7ba1\u7406\u9875\u9762\u5df2\u6253\u5f00\uff0c\u5b8c\u6210\u540e\u70b9\u51fb\u9000\u51fa\u3002";
                OpenBrowserRequested?.Invoke(lease.IpAddress.ToString());
            }
            catch (OperationCanceledException)
            {
                if (_isCleaningUp) return;
                Log("Flow cancelled");
                StatusText = "\u5df2\u53d6\u6d88";
                DetailText = "\u6d41\u7a0b\u5df2\u53d6\u6d88\u3002\u5982\u9700\u9000\u51fa\uff0c\u8bf7\u70b9\u51fb\u9000\u51fa\u5e76\u6062\u590d\u7f51\u5361\u3002";
                ActivityText = "";
                BadgeText = "\u5df2\u53d6\u6d88";
                BadgeColor = "#666";
            }
            catch (Exception ex)
            {
                if (_isCleaningUp) return;
                Log("Flow failed: " + ex.Message);
                StatusText = "\u64cd\u4f5c\u5931\u8d25";
                DetailText = ex.Message;
                BadgeText = "\u5931\u8d25";
                BadgeColor = "#D13438";
                _adapterSelectionEnabled = true;
                _startButtonEnabled = true;
                OnPropertyChanged(nameof(AdapterSelectionEnabled));
                OnPropertyChanged(nameof(StartButtonEnabled));
            }
        }

        private async Task ConfigureAdapterAsync(CancellationToken ct)
        {
            StatusText = "\u6b63\u5728\u914d\u7f6e\u7f51\u5361...";
            ActivityText = "\u6b63\u5728\u5c06\u7f51\u5361\u5207\u6362\u5230 " + _subnetConfig.ServerDisplay;
            BadgeText = "\u5904\u7406\u4e2d";
            BadgeColor = "#0078D4";

            _originalConfig = NetworkConfigManager.CaptureOriginalConfig(_selectedAdapter);
            Log("Config: dhcpEnabled=" + _originalConfig.DhcpEnabled + ", addr=" + _subnetConfig.ServerDisplay);
            if (!_originalConfig.DhcpEnabled)
            {
                await NetworkConfigManager.ForceDhcpBestEffortAsync(_selectedAdapter, _subnetConfig, ct, releaseToolLease: false);
                await Task.Delay(1200, ct);
                _originalConfig = AdapterOriginalConfig.CreateDhcp();
            }

            await NetworkConfigManager.SetStaticForToolAsync(_selectedAdapter, _subnetConfig, ct);
            _dhcpServer = new DhcpServer(_subnetConfig);
            _dhcpServer.Logger = msg => Log("[DHCP] " + msg);
            _dhcpServer.ErrorEncountered += OnDhcpServerError;
            _dhcpServer.Start();
        }

        private void OnDhcpServerError(object sender, string message)
        {
            _dhcpServerError = message;
            Log("[DHCP] Error: " + message);
        }

        private async Task WaitForLinkAsync(CancellationToken ct)
        {
            Log("Link wait started");
            StatusText = "\u8bf7\u63d2\u5165\u7f51\u7ebf";
            ActivityText = "\u8bf7\u7528\u7f51\u7ebf\u8fde\u63a5\u670d\u52a1\u5668\u7684 IPMI \u7ba1\u7406\u53e3\u3002";
            var warned = false;
            var start = DateTime.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                if (await NetworkConfigManager.IsLinkUpAsync(_selectedAdapter, ct))
                {
                    Log("Link detected");
                    return;
                }
                if (!warned && (DateTime.UtcNow - start).TotalSeconds > 60)
                {
                    Log("Link wait still pending after 60 seconds");
                    DetailText = "\u5df2\u7b49\u5f85 60 \u79d2\u672a\u68c0\u6d4b\u5230\u7f51\u7ebf\u8fde\u63a5\u3002";
                    warned = true;
                }
                await Task.Delay(1500, ct);
            }
        }

        private async Task<DhcpLease> WaitForLeaseAsync(CancellationToken ct)
        {
            Log("DHCP lease wait started");
            StatusText = "\u6b63\u5728\u7b49\u5019 BMC \u4e0a\u7ebf";
            ActivityText = "\u7b49\u5f85 IPMI \u8bbe\u5907\u901a\u8fc7 DHCP \u83b7\u53d6\u5730\u5740\uff0c\u6700\u591a 3 \u5206\u949f\u3002";

            var tcs = new TaskCompletionSource<DhcpLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object s, DhcpLease l) => tcs.TrySetResult(l);
            void ErrorHandler(object s, string msg) => tcs.TrySetException(new InvalidOperationException(msg));
            if (_dhcpServer != null) _dhcpServer.LeaseAssigned += Handler;
            if (_dhcpServer != null) _dhcpServer.ErrorEncountered += ErrorHandler;

            try
            {
                if (!string.IsNullOrEmpty(_dhcpServerError))
                    throw new InvalidOperationException(_dhcpServerError);

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                using (var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
                {
                    var lease = await tcs.Task;
                    Log("DHCP lease acquired: IP=" + lease.IpAddress + " MAC=" + MacBytesToString(lease.MacAddress));
                    return lease;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log("DHCP lease wait timed out");
                throw new TimeoutException("\u672a\u83b7\u53d6\u5230 BMC \u5730\u5740\uff0c\u8bf7\u68c0\u67e5\u7f51\u7ebf\u662f\u5426\u6b63\u786e\u8fde\u63a5\u3002");
            }
            finally
            {
                if (_dhcpServer != null) _dhcpServer.LeaseAssigned -= Handler;
                if (_dhcpServer != null) _dhcpServer.ErrorEncountered -= ErrorHandler;
            }
        }

        private static string MacBytesToString(byte[] mac)
        {
            if (mac == null || mac.Length == 0)
                return "none";
            return BitConverter.ToString(mac);
        }

        public async Task<bool> CleanupAsync()
        {
            if (_isClosing || _isCleaningUp) return false;
            _isClosing = true;
            _isCleaningUp = true;
            Log("Cleanup started");
            try
            {
                StatusText = "\u6b63\u5728\u9000\u51fa\u5e76\u6062\u590d\u7f51\u5361...";
                ActivityText = "\u8bf7\u7a0d\u5019...";
                BadgeText = "\u5904\u7406\u4e2d";
                BadgeColor = "#0078D4";

                await DoCleanupAsync();

                Log("Cleanup success");
                StatusText = "\u7f51\u5361\u5df2\u6062\u590d";
                BadgeText = "\u5df2\u5b8c\u6210";
                BadgeColor = "#107C10";
                _isCleanupDone = true;
                return true;
            }
            catch (Exception ex)
            {
                Log("Cleanup failed: " + ex.Message);
                StatusText = "\u6062\u590d\u7f51\u5361\u8bbe\u7f6e\u65f6\u9047\u5230\u95ee\u9898";
                DetailText = ex.Message;
                BadgeText = "\u5931\u8d25";
                BadgeColor = "#D13438";
                _isCleaningUp = false;
                _isClosing = false;
                return false;
            }
        }

        private async Task DoCleanupAsync()
        {
            _flowCts?.Cancel();
            try { await _flowTask; } catch (OperationCanceledException) { } catch { }

            _dhcpServer?.Stop();
            if (_dhcpServer != null)
                _dhcpServer.ErrorEncountered -= OnDhcpServerError;
            _dhcpServer?.Dispose();
            _dhcpServer = null;

            if (_selectedAdapter != null)
            {
                using (var c = new CancellationTokenSource(TimeSpan.FromSeconds(70)))
                {
                    await NetworkConfigManager.ForceDhcpBestEffortAsync(_selectedAdapter, _subnetConfig, c.Token, releaseToolLease: true);
                }
            }
        }

        public void CancelFlow()
        {
            if (_isClosing || _isCleaningUp) return;
            _isCleaningUp = true;
            _flowCts?.Cancel();
            Log("Cancel requested");
            StatusText = "\u6b63\u5728\u53d6\u6d88\u5e76\u6062\u590d\u7f51\u5361...";
            ActivityText = "\u8bf7\u7a0d\u5019...";
            BadgeText = "\u5904\u7406\u4e2d";
            BadgeColor = "#0078D4";
            var _ = CancelCleanupAsync();
        }

        private async Task CancelCleanupAsync()
        {
            try
            {
                await DoCleanupAsync();
                Log("Cancel cleanup success");
                _isFlowStarted = false;
                _discoveredIp = null;
                _adapterSelectionEnabled = true;
                _startButtonEnabled = true;
                OnPropertyChanged(nameof(AdapterSelectionEnabled));
                OnPropertyChanged(nameof(StartButtonEnabled));
                StatusText = "\u5df2\u53d6\u6d88\uff0c\u7f51\u5361\u5df2\u6062\u590d";
                DetailText = "\u53ef\u91cd\u65b0\u9009\u62e9\u7f51\u5361\u5f00\u59cb\u3002";
                BadgeText = "";
                ActivityText = "";
                OnPropertyChanged(nameof(ShowAdapterSelection));
                OnPropertyChanged(nameof(ShowRunning));
                OnPropertyChanged(nameof(ShowIpResult));
                OnPropertyChanged(nameof(DiscoveredIp));
            }
            catch (Exception ex)
            {
                Log("Cancel cleanup failed: " + ex.Message);
                StatusText = "\u53d6\u6d88\u65f6\u6062\u590d\u7f51\u5361\u5931\u8d25";
                DetailText = ex.Message;
                BadgeText = "\u5931\u8d25";
                BadgeColor = "#D13438";
            }
            finally
            {
                _isCleaningUp = false;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    internal sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, Task> _executeAsync;

        public RelayCommand(Action<object> execute) => _execute = execute;
        public RelayCommand(Func<object, Task> executeAsync) => _executeAsync = executeAsync;

        public bool CanExecute(object p) => true;

        public async void Execute(object p)
        {
            if (_executeAsync != null)
                await _executeAsync(p);
            else
                _execute?.Invoke(p);
        }
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
}
