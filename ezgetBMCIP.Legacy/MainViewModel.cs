using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EzGetBmcIp;

namespace EzGetBmcIp.Legacy
{
    public sealed partial class MainViewModel : INotifyPropertyChanged, IRuntimePresentationSource
    {
        int IRuntimePresentationSource.CurrentFirewallRepairStageValue => (int)CurrentFirewallRepairStage;
        private readonly SubnetConfig _subnetConfig = new SubnetConfig();
        private DhcpServer _dhcpServer;
        private CancellationTokenSource _flowCts;
        private WiredAdapter _selectedAdapter;
        private AdapterOriginalConfig _originalConfig;
        private NetworkRecoverySnapshot _recoverySnapshot;
        private SubnetConfig _recoverySubnetConfig;
        private bool _isCleaningUp;
        private string _dhcpServerError;
        private bool _lastDhcpWaitTimedOut;
        private FirewallRiskLevel? _lastTimeoutFirewallRisk;
        private FirewallRiskLevel? _currentFirewallRisk;
        private string _firewallNetworkCategorySnapshot;
        private string _firewallTechnicalDetail = string.Empty;
        private bool _historyRetryUsed;
        private BmcHistoryRecord _historyRetrySuggestion;
        private bool _showSupportBundleAction;
        private bool _hasLongLinkWait;
        private DispatcherTimer _dhcpElapsedTimer;
        private Stopwatch _dhcpWaitStopwatch;
        private DateTime? _dhcpWaitStartedUtc;
        private DateTime? _lastDhcpTimerTickUtc;
        private bool _dhcpTimerLagLogged;
        private string _dhcpElapsedText = "00:00";
        private bool _hasLongDhcpWait;
        private BmcEndpointProbeEvidence _lastEndpointProbeEvidence;
        private int _lastEndpointProbeAttempts;
        private BmcReachabilityResult _lastReachabilityResult;
        private string _reachabilityDiagnostic = string.Empty;
        private string _endpointAdapterResolutionDiagnostic = string.Empty;
        private string _endpointNetworkWarning = string.Empty;

        private Task _flowTask = Task.CompletedTask;
        private Task _endpointProbeTask = Task.CompletedTask;
        private Task _pendingRecoveryTask;
        private bool _isClosing;

        private CancellationTokenSource _startPreflightCts;
        private int _startPreflightGeneration;
        private bool _isStartPreflightBusy;
        private bool _showStartPreflightOverlay;
        private string _startPreflightStageText = string.Empty;

        private bool _isPreparation = true;
        private bool _isFlowStarted;
        private string _initError;

        public SubnetConfig SubnetConfig => _subnetConfig;
        public string VersionText => AppVersionText.Get();
        public CurrentSessionState SessionState { get; } = new CurrentSessionState();

        public bool IsStartPreflightBusy => _isStartPreflightBusy;
        public bool ShowStartPreflightOverlay => _showStartPreflightOverlay;
        public string StartPreflightTitle => "正在检查网络环境";
        public string StartPreflightStageText => _startPreflightStageText;
        public string StartPreflightDetailText => "当前尚未修改网卡。";

        public ModernRuntimePresentation LegacyRuntime => ModernRuntimePresentation.From(this);

        public bool ShowHistoryRetrySuggestion => _historyRetrySuggestion != null;
        public bool ShowSupportBundleAction
        {
            get => _showSupportBundleAction;
            private set
            {
                _showSupportBundleAction = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowStandaloneSupportBundleAction));
            }
        }

        public bool ShowStandaloneSupportBundleAction => ShowSupportBundleAction && !ShowHistoryRetrySuggestion;

        public string HistoryRetrySuggestionText => _historyRetrySuggestion == null
            ? string.Empty
            : "本次等待 DHCP 已超时；所选本机网卡曾在下列网段确认过地址可达。";
        public string HistoryAdapterName => CurrentAdapterName;
        public string HistoryLocalAddress => _historyRetrySuggestion == null ? string.Empty : _historyRetrySuggestion.LocalAddress + " / 24";
        public string HistoryBmcAddress => _historyRetrySuggestion == null ? string.Empty : _historyRetrySuggestion.BmcAddress;
        public string HistoryEndpointText => _historyRetrySuggestion == null
            ? string.Empty
            : string.IsNullOrWhiteSpace(_historyRetrySuggestion.EndpointScheme)
                ? "地址可达"
                : _historyRetrySuggestion.EndpointScheme.ToUpperInvariant() + " · " + _historyRetrySuggestion.EndpointPort;
        public string HistoryLastConfirmedText => _historyRetrySuggestion == null
            ? string.Empty
            : _historyRetrySuggestion.LastConfirmedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        public SessionPagePresentation CurrentSessionPresentation =>
            SessionPresentation.GetSessionPagePresentation(
                SessionState,
                ShowHistoryRetrySuggestion,
                PreferredBmcScheme);

        public SessionPageKind CurrentSessionPage => CurrentSessionPresentation.Page;
        public int CurrentStepIndex
        {
            get
            {
                switch (SessionState.Workflow)
                {
                    case DiscoveryWorkflowState.WaitingForLink:
                    case DiscoveryWorkflowState.ConfiguringNetwork:
                        return 1;
                    case DiscoveryWorkflowState.WaitingForDhcp:
                        return 2;
                    case DiscoveryWorkflowState.ProbingEndpoint:
                    case DiscoveryWorkflowState.EndpointReachable:
                    case DiscoveryWorkflowState.EndpointUnreachable:
                        return 3;
                    default:
                        if (SessionState.Failure == null) return 0;
                        switch (SessionState.Failure.Kind)
                        {
                            case FailureKind.DhcpTimedOut:
                            case FailureKind.DhcpListenerFailed:
                                return 2;
                            case FailureKind.EndpointProbeError:
                            case FailureKind.EndpointAdapterUnavailable:
                                return 3;
                            default:
                                return 1;
                        }
                }
            }
        }
        public string SessionPageTitle => CurrentSessionPresentation.Title;
        public string SessionPageSummary => CurrentSessionPresentation.Summary;
        public PageActionKind RecommendedActionKind => CurrentSessionPresentation.RecommendedActionKind;

        public NetworkStatusPresentation NetworkStatusPresentation =>
            SessionPresentation.GetNetworkStatusPresentation(SessionState.Network, CurrentAdapterDisplayName);

        public string NetworkStatusText => NetworkStatusPresentation.Text;
        public PresentationSeverity NetworkStatusSeverity => NetworkStatusPresentation.Severity;

        public string CandidateSourceText => SessionPresentation.GetCandidateSourceText(SessionState.CandidateAddress);
        public string BrowserStatusText => SessionPresentation.GetBrowserStatusPresentation(SessionState.BrowserLaunchResult).Text;
        public PresentationSeverity BrowserStatusSeverity =>
            SessionPresentation.GetBrowserStatusPresentation(SessionState.BrowserLaunchResult).Severity;
        public string EndpointProtocolPortText => _lastReachabilityResult == null
            ? SessionPresentation.GetEndpointProtocolPortText(PreferredBmcScheme)
            : _lastReachabilityResult.HttpsPortOpen && _lastReachabilityResult.HttpPortOpen
                ? "TCP 443、80"
                : _lastReachabilityResult.HttpsPortOpen
                    ? "TCP 443"
                    : _lastReachabilityResult.HttpPortOpen ? "TCP 80" : "80/443 未连通";

        public string EndpointVerificationText
        {
            get
            {
                var reachability = _lastReachabilityResult;
                if (reachability != null)
                {
                    if (reachability.IsReachable)
                    {
                        var transport = reachability.HttpsPortOpen && reachability.HttpPortOpen
                            ? "TCP 443、80"
                            : reachability.HttpsPortOpen
                                ? "TCP 443"
                                : reachability.HttpPortOpen ? "TCP 80" : "Ping";
                        return "地址已确认可达（" + transport + "）。" +
                            (reachability.PingSucceeded ? string.Empty : "设备未响应 ICMP Ping。");
                    }

                    return "已取得候选地址，但 5 秒内未收到 Ping 或 TCP 80/443 的成功响应。";
                }

                var evidence = _lastEndpointProbeEvidence;
                if (evidence == null)
                    return "尚未完成地址可达性检测。";

                if (evidence.HttpResponseReceived && BmcEndpointProbe.IsAcceptedManagementHttpStatus(evidence.HttpStatusCode))
                    return "兼容性严格探测曾从所选直连网卡收到 " + (evidence.TlsEstablished ? "HTTPS" : "HTTP") +
                        " 响应：HTTP " + evidence.HttpStatusCode + "；邻居 MAC：" +
                        (string.IsNullOrWhiteSpace(evidence.NeighborMac) ? "未记录" : evidence.NeighborMac) +
                        "。当前成功判定以 Ping/TCP 可达性为准。";

                if (evidence.HttpResponseReceived)
                    return "兼容性严格探测收到 HTTP " + evidence.HttpStatusCode +
                        "；该网页响应不参与当前地址可达性判定。";

                return "兼容性严格探测未收到 HTTP/HTTPS 响应；当前地址可达性由 Ping/TCP 80/443 决定，最后阶段：" + evidence.FailureStage + "。";
            }
        }

        public string EndpointVerificationDiagnosticText
        {
            get
            {
                if (_lastReachabilityResult != null)
                    return _reachabilityDiagnostic;

                var evidence = _lastEndpointProbeEvidence;
                if (evidence == null)
                    return string.IsNullOrWhiteSpace(_endpointAdapterResolutionDiagnostic)
                        ? "(no endpoint probe evidence)"
                        : "adapterResolution=" + _endpointAdapterResolutionDiagnostic;

                return (string.IsNullOrWhiteSpace(_endpointAdapterResolutionDiagnostic)
                        ? string.Empty
                        : "adapterResolution=" + _endpointAdapterResolutionDiagnostic + "; ") +
                    "attempts=" + _lastEndpointProbeAttempts +
                    "; target=" + evidence.TargetAddress +
                    "; source=" + evidence.SourceAddress +
                    "; requestedInterface=" + evidence.RequestedInterfaceIndex +
                    "; bestRouteInterface=" + evidence.BestRouteInterfaceIndex +
                    "; neighborResolved=" + evidence.NeighborResolved +
                    "; neighborMac=" + evidence.NeighborMac +
                    "; peerMacMatchesExpected=" + (evidence.PeerMacMatchesExpected.HasValue ? evidence.PeerMacMatchesExpected.Value.ToString() : "unknown") +
                    "; tcp=" + evidence.TcpConnected +
                    "; tls=" + evidence.TlsEstablished +
                    "; tlsProtocol=" + evidence.TlsProtocol +
                    "; http=" + evidence.HttpResponseReceived +
                    "; status=" + evidence.HttpStatusCode +
                    "; failure=" + evidence.FailureStage +
                    (string.IsNullOrWhiteSpace(evidence.FailureDetail) ? string.Empty : "; detail=" + evidence.FailureDetail);
            }
        }

        public string EndpointNetworkWarning => _endpointNetworkWarning;
        public bool HasEndpointNetworkWarning => !string.IsNullOrWhiteSpace(_endpointNetworkWarning);

        public FailurePresentation FailurePresentation => SessionPresentation.GetFailurePresentation(SessionState.Failure);
        public string FailureTitle => FailurePresentation.Title;
        public string FailureSummary => FailurePresentation.Summary;
        public PageActionKind FailureRecommendedActionKind => FailurePresentation.RecommendedActionKind;
        public string FailureTechnicalDetail => SessionState.Failure == null
            ? string.Empty
            : SessionState.Failure.TechnicalDetail ?? string.Empty;
        public bool HasFailureTechnicalDetail => !string.IsNullOrWhiteSpace(FailureTechnicalDetail);

        public string FirewallSummary => SessionPresentation.GetFirewallPresentation(_currentFirewallRisk).Summary;
        public PresentationSeverity FirewallSeverity => SessionPresentation.GetFirewallPresentation(_currentFirewallRisk).Severity;
        public bool HasFirewallAssessment => _currentFirewallRisk.HasValue;
        internal string FirewallNetworkCategorySnapshot => _firewallNetworkCategorySnapshot;
        public string FirewallTechnicalDetail => _firewallTechnicalDetail;
        public bool HasFirewallTechnicalDetail => !string.IsNullOrWhiteSpace(FirewallTechnicalDetail);

        public string CurrentAdapterName => string.IsNullOrWhiteSpace(CurrentAdapterDisplayName)
            ? "所选网卡"
            : CurrentAdapterDisplayName;
        public string LinkStatusText => _hasLongLinkWait ? "仍未检测到物理连接" : "正在等待物理连接";
        public bool ShowLongLinkWaitHint => _hasLongLinkWait && CurrentSessionPage == SessionPageKind.WaitingForLink;

        public string DhcpElapsedText
        {
            get => _dhcpElapsedText;
            private set
            {
                if (_dhcpElapsedText == value) return;
                _dhcpElapsedText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LegacyRuntime));
            }
        }

        public string DhcpMaximumWaitText => "03:00";
        public bool ShowLongDhcpWaitHint => _hasLongDhcpWait && CurrentSessionPage == SessionPageKind.WaitingForDhcp;
        public bool ShowDhcpFirewallEvidence => ShowLongDhcpWaitHint && HasFirewallAssessment;
        public string DhcpEvidenceText => SessionPresentation.GetDhcpEvidencePresentation(SessionState.DhcpEvidence).Text;
        public PresentationSeverity DhcpEvidenceSeverity =>
            SessionPresentation.GetDhcpEvidencePresentation(SessionState.DhcpEvidence).Severity;
        public string DhcpTimeoutFollowUpText => SessionPresentation.GetDhcpTimeoutFollowUpText(_currentFirewallRisk);
        public bool HasDhcpTimeoutFollowUp => !string.IsNullOrWhiteSpace(DhcpTimeoutFollowUpText);

        public string FailureRecommendedActionText => SessionPresentation.GetFailureRecommendedActionText(
            SessionState.Failure,
            SessionState.HasCandidateAddress,
            SessionState.Network);
        public string FailureNetworkImpactText =>
            SessionPresentation.GetFailureNetworkImpactText(SessionState.Network);
        public bool CanRetryEndpointFromFailure =>
            CurrentSessionPage == SessionPageKind.Failure
            && SessionState.Failure != null
            && (SessionState.Failure.Kind == FailureKind.EndpointProbeError
                || SessionState.Failure.Kind == FailureKind.EndpointAdapterUnavailable)
            && SessionState.HasCandidateAddress;

        public ObservableCollection<WiredAdapter> Adapters { get; } = new ObservableCollection<WiredAdapter>();
        private WiredAdapter _selectedAdapterItem;
        public WiredAdapter SelectedAdapterItem
        {
            get => _selectedAdapterItem;
            set
            {
                _selectedAdapterItem = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentAdapterName));
                OnPropertyChanged(nameof(NetworkStatusText));
                OnPropertyChanged(nameof(RecoveryAdapterDisplayName));
                OnPropertyChanged(nameof(HistoryAdapterName));
            }
        }

        private string _statusText = "IPMI/BMC \u76f4\u8fde\u52a9\u624b";
        public string StatusText { get => UsesSessionPresentation ? SessionPageTitle : _statusText; set { _statusText = value; OnPropertyChanged(); } }

        private string _detailText = "\u9009\u62e9\u7f51\u5361\uff0c\u914d\u7f6e\u7f51\u6bb5\uff0c\u81ea\u52a8\u83b7\u53d6 BMC \u5730\u5740\u3002";
        public string DetailText { get => UsesSessionPresentation ? SessionPageSummary : _detailText; set { _detailText = value; OnPropertyChanged(); } }

        private string _activityText = "";
        public string ActivityText { get => _activityText; set { _activityText = value; OnPropertyChanged(); OnPropertyChanged(nameof(LegacyRuntime)); } }

        private string _badgeText = "\u7b49\u5f85\u4e2d";
        public string BadgeText { get => _badgeText; set { _badgeText = value; OnPropertyChanged(); } }

        private string _badgeColor = "#666";
        public string BadgeColor { get => _badgeColor; set { _badgeColor = value; OnPropertyChanged(); } }

        private bool _adapterSelectionEnabled = true;
        public bool AdapterSelectionEnabled { get => _adapterSelectionEnabled && !IsFirewallRepairBusy && !IsStartPreflightBusy; set { _adapterSelectionEnabled = value; OnPropertyChanged(); } }

        private bool _startButtonEnabled = true;
        public bool StartButtonEnabled { get => _startButtonEnabled && !IsFirewallRepairBusy && !IsStartPreflightBusy; set { _startButtonEnabled = value; OnPropertyChanged(); } }

        private bool _isCleanupDone;
        public string DiscoveredIp
        {
            get => SessionState.CandidateAddress == null ? null : SessionState.CandidateAddress.IPv4Address;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                    ClearCandidateAddress();
                else
                    SetCandidateAddress(value, CandidateAddressSource.DhcpAck);
            }
        }

        private string _preferredBmcScheme = "https";
        public string PreferredBmcScheme
        {
            get => _preferredBmcScheme;
            private set
            {
                _preferredBmcScheme = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiscoveredIpUrl));
                OnPropertyChanged(nameof(EndpointProtocolPortText));
                OnPropertyChanged(nameof(LegacyRuntime));
            }
        }

        private string _endpointStatusText = "等待 BMC 地址的可达性检测。";
        public string EndpointStatusText
        {
            get => _endpointStatusText;
            set { _endpointStatusText = value; OnPropertyChanged(); }
        }

        private bool _isEndpointProbeRunning;
        public bool IsEndpointProbeRunning
        {
            get => _isEndpointProbeRunning;
            private set { _isEndpointProbeRunning = value; OnPropertyChanged(); }
        }

        public string DiscoveredIpUrl => string.IsNullOrEmpty(DiscoveredIp)
            ? ""
            : PreferredBmcScheme + "://" + DiscoveredIp;
        public bool IsIpDiscovered => SessionState.HasCandidateAddress;

        // These are display-only projections. Page selection itself remains the
        // shared CurrentSessionPage projection, rather than a second set of
        // Legacy business-state tests.
        private bool IsDedicatedSessionPage =>
            CurrentSessionPage == SessionPageKind.RestoreFailed
            || CurrentSessionPage == SessionPageKind.EndpointReachable
            || CurrentSessionPage == SessionPageKind.EndpointUnreachable
            || CurrentSessionPage == SessionPageKind.WaitingForLink
            || CurrentSessionPage == SessionPageKind.WaitingForDhcp
            || CurrentSessionPage == SessionPageKind.ProbingEndpoint
            || CurrentSessionPage == SessionPageKind.DhcpTimedOut
            || CurrentSessionPage == SessionPageKind.HistoryRetrySuggestion
            || CurrentSessionPage == SessionPageKind.Failure
            || CurrentSessionPage == SessionPageKind.Restoring;

        public bool ShowLegacyRuntimeHost => !_isCleanupDone &&
            (_isFlowStarted || CurrentSessionPage == SessionPageKind.Restoring || CurrentSessionPage == SessionPageKind.RestoreFailed);
        public bool ShowAdapterFirewallNotice => ShowFirewallRepair && !ShowLegacyRuntimeHost;

        public bool ShowLegacyHeader => !_isFlowStarted && !IsDedicatedSessionPage;
        public bool ShowPreparationContent => _isPreparation && !_isFlowStarted;
        public bool ShowAdapterSelectionContent => !_isPreparation && !_isFlowStarted && !IsDedicatedSessionPage;
        public bool ShowLegacyRuntimeCard =>
            _isFlowStarted && !_isCleanupDone && CurrentSessionPage == SessionPageKind.ConfiguringNetwork;

        public string AdapterCardLine => _selectedAdapter?.DisplayName ?? "";
        public string RecoveryAdapterDisplayName => CurrentAdapterDisplayName;

        public string RecoveryOriginalConfigDetails
        {
            get
            {
                var config = _originalConfig;
                if (config == null && _recoverySnapshot != null)
                {
                    try { config = _recoverySnapshot.ToOriginalConfig(); }
                    catch { return string.Empty; }
                }
                if (config == null) return string.Empty;

                var mode = config.DhcpEnabled ? "DHCP 自动获取" : "静态 IPv4";
                var addresses = config.StaticAddresses.Count == 0
                    ? "无已记录地址"
                    : string.Join("；", config.StaticAddresses.Select(item => item.Address + " / " + item.Mask));
                var gateways = config.Gateways.Count == 0
                    ? "无"
                    : string.Join("、", config.Gateways);
                var dns = config.DnsServersFromDhcp
                    ? "DHCP 自动获取"
                    : config.DnsServers.Count == 0
                        ? "无"
                        : string.Join("、", config.DnsServers);
                return "原 IPv4：" + mode + "；地址：" + addresses
                    + "\n原网关：" + gateways + "\n原 DNS：" + dns;
            }
        }

        private bool _isAdvancedSubnetExpanded;
        public bool IsAdvancedSubnetExpanded
        {
            get { return _isAdvancedSubnetExpanded; }
            set
            {
                if (_isAdvancedSubnetExpanded == value) return;
                _isAdvancedSubnetExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AdvancedSubnetToggleText));
            }
        }

        public string AdvancedSubnetToggleText => IsAdvancedSubnetExpanded ? "收起" : "修改";
        public string ExitButtonText => SessionPresentation.GetExitActionText(SessionState.ExitIntent);
        public bool IsExitActionEnabled =>
            !IsFirewallRepairBusy && !_isCleaningUp && SessionPresentation.IsExitActionEnabled(SessionState.ExitIntent);

        public ICommand StartCommand { get; }
        public ICommand GoNextCommand { get; }
        public ICommand ExitCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand RetryEndpointCommand { get; }
        public ICommand CopyIpCommand { get; }
        public ICommand OpenHttpsCommand { get; }
        public ICommand OpenHttpCommand { get; }
        public ICommand OpenManagementPageCommand { get; }
        public ICommand PrepareHistoryRetryCommand { get; }
        public ICommand ToggleAdvancedSubnetCommand { get; }

        public event Action RequestClose;
        public event Action<string> OpenBrowserRequested;
        internal event Func<ConsentNotice, bool> ConsentRequested;
        internal Func<IPAddress, IPAddress, CancellationToken, Task<BmcReachabilityResult>> ReachabilityProbeForTests { get; set; }

        public MainViewModel()
        {
            StartCommand = new RelayCommand(async _ => await StartFlowAsync());
            GoNextCommand = new RelayCommand(_ => GoNext());
            ExitCommand = new RelayCommand(_ => RequestClose?.Invoke());
            CancelCommand = new RelayCommand(_ => CancelFlow());
            RetryEndpointCommand = new RelayCommand(async _ =>
            {
                _endpointProbeTask = RetryEndpointProbeAsync();
                await _endpointProbeTask;
            });
            CopyIpCommand = new RelayCommand(_ => CopyIp());
            OpenHttpsCommand = new RelayCommand(_ => OpenBrowserForScheme("https"));
            OpenHttpCommand = new RelayCommand(_ => OpenBrowserForScheme("http"));
            OpenManagementPageCommand = new RelayCommand(_ => OpenBrowserForScheme(PreferredBmcScheme));
            PrepareHistoryRetryCommand = new RelayCommand(async _ => await PrepareHistoryRetryAsync());
            ToggleAdvancedSubnetCommand = new RelayCommand(_ => IsAdvancedSubnetExpanded = !IsAdvancedSubnetExpanded);
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
                try
                {
                    _pendingRecoveryTask = RecoverPendingNetworkConfigurationAsync(adapters);
                    await _pendingRecoveryTask;
                }
                catch (Exception ex)
                {
                    FailSession(FailureKind.PendingRecoveryFailed, ex.Message);
                    throw;
                }
                finally
                {
                    _pendingRecoveryTask = null;
                }
                if (adapters.Count == 0)
                {
                    FailSession(FailureKind.AdapterUnavailable, "No wired adapter was available during initialization.");
                    throw new InvalidOperationException("\u672a\u68c0\u6d4b\u5230\u53ef\u7528\u7f51\u5361");
                }
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
                if (!SessionState.HasFailure)
                    FailSession(FailureKind.InitializationFailed, ex.Message);
                _initError = ex.Message;
                DetailText = ex.Message;
                StartButtonEnabled = false;
            }
        }

        private void GoNext()
        {
            if (!string.IsNullOrWhiteSpace(_initError))
            {
                StatusText = "操作失败";
                DetailText = _initError;
                return;
            }

            if (!RequestConsent(ConsentNotice.CreateModernUsageRisk()))
            {
                Log("Usage risk consent declined");
                return;
            }

            _isPreparation = false;
            StatusText = "选择连接 BMC 的网卡";
            DetailText = "选择直连服务器管理口的有线网卡，然后点击“开始”。";
            OnPropertyChanged(nameof(ShowPreparationContent));
            OnPropertyChanged(nameof(ShowAdapterSelectionContent));
        }

        private async Task RecoverPendingNetworkConfigurationAsync(System.Collections.Generic.IReadOnlyList<WiredAdapter> adapters)
        {
            NetworkRecoverySnapshot snapshot;
            string loadError;
            if (!NetworkRecoveryStore.TryLoad(out snapshot, out loadError))
            {
                if (!string.IsNullOrWhiteSpace(loadError))
                {
                    throw new InvalidOperationException(
                        "检测到损坏的网卡恢复记录。为避免覆盖原设置，已停止新的操作。恢复记录：" +
                        NetworkRecoveryStore.RecoveryFilePath + "；错误：" + loadError);
                }
                return;
            }

            StatusText = "正在恢复上次未完成的网卡配置...";
            DetailText = "检测到程序上次未正常结束，正在恢复网卡「" + snapshot.AdapterName + "」。";
            Log("Pending recovery found: session=" + snapshot.SessionId + " adapter=" + snapshot.AdapterName);
            try
            {
                _recoverySnapshot = snapshot;
                _selectedAdapter = snapshot.ToAdapter();
                _originalConfig = snapshot.ToOriginalConfig(_selectedAdapter);
                _recoverySubnetConfig = snapshot.ToSubnetConfig();
                SetNetworkState(NetworkLifecycleState.Restoring);
                using (var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(70)))
                {
                    await NetworkRecoveryStore.ExecuteWithRecoveryLockAsync(async () =>
                    {
                        NetworkRecoverySnapshot currentSnapshot;
                        string currentError;
                        if (!NetworkRecoveryStore.TryLoad(out currentSnapshot, out currentError))
                        {
                            if (!string.IsNullOrWhiteSpace(currentError))
                                throw new InvalidDataException(currentError);
                            return;
                        }

                        if (!currentSnapshot.SessionId.Equals(snapshot.SessionId, StringComparison.OrdinalIgnoreCase))
                            return;

                        var adapter = await Task.Run(
                            () => NetworkConfigManager.ResolveCurrentAdapter(currentSnapshot.ToAdapter()),
                            recoveryCts.Token);
                        await NetworkConfigManager.RestoreOriginalConfigAsync(
                            adapter,
                            currentSnapshot.ToOriginalConfig(adapter),
                            currentSnapshot.ToSubnetConfig(),
                            recoveryCts.Token);
                        NetworkRecoveryStore.DeleteIfSessionMatches(currentSnapshot.SessionId);
                    }, recoveryCts.Token);
                }
                StatusText = "上次网卡配置已恢复";
                DetailText = "异常退出留下的网络配置已经处理，可以继续使用。";
                Log("Pending recovery completed: session=" + snapshot.SessionId);
                SetNetworkState(NetworkLifecycleState.Untouched);
                _recoverySnapshot = null;
                _recoverySubnetConfig = null;
                _selectedAdapter = null;
                _originalConfig = null;
            }
            catch (Exception ex)
            {
                SetNetworkState(NetworkLifecycleState.RestoreFailed);
                StatusText = "上次网卡配置恢复失败";
                DetailText = "请重新连接网卡「" + snapshot.AdapterName + "」后重启程序。恢复记录会继续保留。";
                throw new InvalidOperationException(
                    "检测到上次未完成的网卡配置，但自动恢复失败。请重新连接原网卡「" +
                    snapshot.AdapterName + "」后重试。原始错误：" + ex.Message,
                    ex);
            }
        }

        private async Task StartFlowAsync()
        {
            if (IsFirewallRepairBusy || IsStartPreflightBusy) return;
            SetFirewallRepairFeedback("");
            if (SelectedAdapterItem == null) return;
            if (!_subnetConfig.IsPrivateSubnet)
            {
                var message = _subnetConfig.ValidationError ?? "\u81ea\u5b9a\u4e49\u7f51\u6bb5\u65e0\u6548\u3002";
                Log("Flow blocked: invalid subnet " + _subnetConfig.ServerDisplay + " - " + message);
                StatusText = "\u7f51\u6bb5\u4e0d\u53ef\u7528";
                DetailText = message + " \u516c\u7f51\u5730\u5740\u53ef\u80fd\u88ab\u7cfb\u7edf\u4ee3\u7406\u6216\u8def\u7531\u7b56\u7565\u62e6\u622a\uff0c\u76f4\u8fde\u573a\u666f\u8bf7\u4f7f\u7528\u79c1\u6709\u7f51\u6bb5\u3002";
                BadgeText = "\u7f51\u6bb5\u9519\u8bef";
                BadgeColor = "#D13438";
                return;
            }

            ClearCurrentFirewallAssessment();
            _firewallNetworkCategorySnapshot = null;
            var preflightAdapter = SelectedAdapterItem;
            using (var preflightCts = new CancellationTokenSource())
            {
                _startPreflightCts = preflightCts;
                var preflightToken = preflightCts.Token;
                var generation = ++_startPreflightGeneration;
                SetStartPreflightStage("正在读取所选网卡配置…");
                SetStartPreflightBusy(true);
                _ = ShowStartPreflightOverlayAfterDelayAsync(generation, preflightToken);

                AdapterOriginalConfig originalConfig;
                var originalConfigCaptured = false;
                try
                {
                    await Task.Yield();
                    preflightToken.ThrowIfCancellationRequested();
                    originalConfig = await Task.Run(
                        () => NetworkConfigManager.CaptureOriginalConfig(preflightAdapter),
                        preflightToken).ConfigureAwait(true);
                    originalConfigCaptured = true;

                    preflightToken.ThrowIfCancellationRequested();
                    SetStartPreflightStage("正在检查防火墙规则…");
                    var firewallAssessment = await AssessFirewallAsync(preflightAdapter, preflightToken).ConfigureAwait(true);
                    preflightToken.ThrowIfCancellationRequested();
                    CaptureFirewallNetworkCategorySnapshot(firewallAssessment);
                    SetCurrentFirewallAssessment(firewallAssessment);
                    SetStartPreflightStage("检查完成，正在显示风险告知…");
                    _showStartPreflightOverlay = false;
                    OnPropertyChanged(nameof(ShowStartPreflightOverlay));

                    if (!RequestConsent(ConsentNotice.CreateModernNetworkChange(
                            preflightAdapter, _subnetConfig, originalConfig, firewallAssessment)))
                    {
                        Log("Network change consent declined");
                        return;
                    }

                    _selectedAdapter = preflightAdapter;
                    _originalConfig = originalConfig;
                }
                catch (OperationCanceledException) when (preflightToken.IsCancellationRequested)
                {
                    Log("Start preflight cancelled");
                    return;
                }
                catch (Exception ex)
                {
                    Log("Original configuration or firewall preflight failed: " + ex.Message);
                    FailSession(originalConfigCaptured ? FailureKind.Unexpected : FailureKind.OriginalConfigurationCaptureFailed, ex.Message);
                    StatusText = originalConfigCaptured ? "无法检查网络环境" : "无法读取网卡原始配置";
                    DetailText = "为避免覆盖现有网络设置，已停止操作：" + ex.Message;
                    BadgeText = "无法确认";
                    BadgeColor = "#D13438";
                    return;
                }
                finally
                {
                    preflightCts.Cancel();
                    if (ReferenceEquals(_startPreflightCts, preflightCts))
                        _startPreflightCts = null;
                    SetStartPreflightBusy(false);
                }
            }

            _recoverySnapshot = null;
            _recoverySubnetConfig = null;
            ResetSessionStateForNewFlow();
            _adapterSelectionEnabled = false;
            _startButtonEnabled = false;
            _isFlowStarted = true;
            OnPropertyChanged(nameof(ShowAdapterSelectionContent));
            OnPropertyChanged(nameof(ShowLegacyHeader));
            OnPropertyChanged(nameof(ShowLegacyRuntimeHost));
            OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
            OnPropertyChanged(nameof(ShowLegacyRuntimeCard));
            OnPropertyChanged(nameof(AdapterCardLine));
            _flowCts = new CancellationTokenSource();
            _dhcpServerError = null;
            _lastDhcpWaitTimedOut = false;
            _lastTimeoutFirewallRisk = null;
            SetHistoryRetrySuggestion(null);
            ShowSupportBundleAction = false;
            PreferredBmcScheme = "https";
            EndpointStatusText = "等待 BMC 地址的可达性检测。";
            SetEndpointProbeOutcome(null);
            _flowTask = RunFlowAsync(_flowCts.Token);
        }

        private async Task RunFlowAsync(CancellationToken ct)
        {
            Log("Flow started, adapter: " + (_selectedAdapter?.Name ?? "null") + ", subnet: " + _subnetConfig.ServerDisplay);
            try
            {
                await RunLinkThenConfigureAsync(WaitForLinkAsync, ConfigureAdapterAsync, ct);
                ct.ThrowIfCancellationRequested();
                var discovery = await DiscoverBmcAddressAsync(ct);
                ct.ThrowIfCancellationRequested();
                Log("Flow candidate discovered, BMC IP: " + discovery.IpAddress);
                if (discovery.Reachability != null)
                    await PresentReachabilityAsync(discovery.IpAddress, discovery.Reachability, true, ct);
                else
                    await ProbeBmcEndpointAsync(discovery.IpAddress, true, ct);
            }
            catch (OperationCanceledException)
            {
                MarkFlowCancelled();
                if (_isCleaningUp) return;
                Log("Flow cancelled");
                StatusText = "\u5df2\u53d6\u6d88";
                DetailText = "\u6d41\u7a0b\u5df2\u53d6\u6d88\u3002\u5982\u9700\u9000\u51fa\uff0c\u8bf7\u70b9\u51fb\u9000\u51fa\u5e76\u6062\u590d\u7f51\u5361\u3002";
                ActivityText = "";
                BadgeText = "\u5df2\u53d6\u6d88";
                BadgeColor = "#666";
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                MarkFlowCancelled();
                if (_isCleaningUp) return;
                Log("Flow cancellation won over a concurrent error: " + ex.Message);
                StatusText = "\u5df2\u53d6\u6d88";
                DetailText = "\u6d41\u7a0b\u5df2\u53d6\u6d88\u3002";
                ActivityText = "";
                BadgeText = "\u5df2\u53d6\u6d88";
                BadgeColor = "#666";
            }
            catch (Exception ex)
            {
                if (_isCleaningUp) return;
                Log("Flow failed: " + ex.Message);
                if (!SessionState.HasFailure)
                    FailSession(FailureKind.Unexpected, ex.Message);
                else
                    SetWorkflowState(DiscoveryWorkflowState.Failed);
                StatusText = "\u64cd\u4f5c\u5931\u8d25";
                DetailText = ex.Message + (SessionState.RequiresNetworkRecovery
                    ? " \u7f51\u5361\u5df2\u7ecf\u5f00\u59cb\u4fee\u6539\uff0c\u8bf7\u5148\u53d6\u6d88\u6216\u9000\u51fa\u6267\u884c\u6062\u590d\uff1b\u6062\u590d\u6210\u529f\u524d\u4e0d\u80fd\u91cd\u65b0\u5f00\u59cb\u3002"
                    : "");
                BadgeText = "\u5931\u8d25";
                BadgeColor = "#D13438";
                ShowSupportBundleAction = true;
                if (_lastDhcpWaitTimedOut)
                    OfferHistoryRetryIfEligible();
                _adapterSelectionEnabled = !SessionState.RequiresCleanup;
                _startButtonEnabled = !SessionState.RequiresCleanup;
                OnPropertyChanged(nameof(AdapterSelectionEnabled));
                OnPropertyChanged(nameof(StartButtonEnabled));
            }
        }

        private async Task ConfigureAdapterAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_selectedAdapter == null || _originalConfig == null)
                throw new InvalidOperationException("未确认网卡原始配置，已停止修改网络设置。");

            SetWorkflowState(DiscoveryWorkflowState.ConfiguringNetwork);
            try
            {
                _selectedAdapter = await Task.Run(
                    () => NetworkConfigManager.ResolveCurrentAdapter(_selectedAdapter),
                    ct).ConfigureAwait(true);
                _originalConfig = await Task.Run(
                    () => NetworkConfigManager.CaptureOriginalConfig(_selectedAdapter),
                    ct).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                SetFailure(FailureKind.OriginalConfigurationCaptureFailed, ex.Message);
                throw;
            }

            StatusText = "\u6b63\u5728\u914d\u7f6e\u7f51\u5361...";
            ActivityText = "已确认原始配置，正在将网卡切换到 " + _subnetConfig.ServerDisplay;
            BadgeText = "\u5904\u7406\u4e2d";
            BadgeColor = "#0078D4";

            Log("Config: dhcpEnabled=" + _originalConfig.DhcpEnabled +
                ", dnsFromDhcp=" + _originalConfig.DnsServersFromDhcp +
                ", gatewayMetrics=" + _originalConfig.GatewayMetrics.Count +
                ", addr=" + _subnetConfig.ServerDisplay);
            _recoverySnapshot = NetworkRecoveryStore.Save(_selectedAdapter, _originalConfig, _subnetConfig);
            Log("Recovery snapshot saved: session=" + _recoverySnapshot.SessionId);
            try
            {
                NetworkRecoveryStore.StartWatchdog(_recoverySnapshot, Log);
            }
            catch
            {
                NetworkRecoveryStore.DeleteIfSessionMatches(_recoverySnapshot.SessionId);
                _recoverySnapshot = null;
                throw;
            }

            SetNetworkState(NetworkLifecycleState.RecoveryPrepared);
            SetNetworkState(NetworkLifecycleState.TemporaryConfigurationMayBeActive);
            Log("Adapter mutation started after Link UP");
            try
            {
                await NetworkConfigManager.SetStaticForToolAsync(_selectedAdapter, _subnetConfig, ct);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetFailure(FailureKind.TemporaryNetworkConfigurationFailed, ex.Message);
                throw;
            }

            SetNetworkState(NetworkLifecycleState.TemporaryConfigurationActive);
            try
            {
                _dhcpServer = new DhcpServer(_subnetConfig, _selectedAdapter);
                _dhcpServer.Logger = msg => Log("[DHCP] " + msg);
                _dhcpServer.ErrorEncountered += OnDhcpServerError;
                _dhcpServer.Start();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetFailure(FailureKind.DhcpListenerFailed, ex.Message);
                throw;
            }
        }

        private void SetStartPreflightBusy(bool value)
        {
            if (_isStartPreflightBusy == value)
                return;

            _isStartPreflightBusy = value;
            if (!value)
            {
                _showStartPreflightOverlay = false;
                OnPropertyChanged(nameof(ShowStartPreflightOverlay));
            }
            OnPropertyChanged(nameof(IsStartPreflightBusy));
            OnPropertyChanged(nameof(AdapterSelectionEnabled));
            OnPropertyChanged(nameof(StartButtonEnabled));
            OnPropertyChanged(nameof(IsExitActionEnabled));
        }

        private void SetStartPreflightStage(string value)
        {
            _startPreflightStageText = value ?? string.Empty;
            OnPropertyChanged(nameof(StartPreflightStageText));
        }

        private async Task ShowStartPreflightOverlayAfterDelayAsync(int generation, CancellationToken token)
        {
            try
            {
                await Task.Delay(150, token).ConfigureAwait(true);
                if (generation == _startPreflightGeneration && IsStartPreflightBusy && !token.IsCancellationRequested)
                {
                    _showStartPreflightOverlay = true;
                    OnPropertyChanged(nameof(ShowStartPreflightOverlay));
                }
            }
            catch (OperationCanceledException)
            {
                // The owning preflight will clear the overlay and busy state.
            }
        }

        private void OnDhcpServerError(object sender, string message)
        {
            _dhcpServerError = message;
            Log("[DHCP] Error: " + message);
        }

        private async Task WaitForLinkAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Log("Link wait started");
            SetLongLinkWait(false);
            SetWorkflowState(DiscoveryWorkflowState.WaitingForLink);
            StatusText = "\u8bf7\u63d2\u5165\u7f51\u7ebf";
            ActivityText = "\u8bf7\u7528\u7f51\u7ebf\u8fde\u63a5\u670d\u52a1\u5668\u7684 IPMI \u7ba1\u7406\u53e3\u3002";
            var start = DateTime.UtcNow;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (await NetworkConfigManager.IsLinkUpAsync(_selectedAdapter, ct))
                    {
                        Log("Link detected");
                        return;
                    }
                    if (!_hasLongLinkWait && (DateTime.UtcNow - start).TotalSeconds > 60)
                    {
                        Log("Link wait still pending after 60 seconds");
                        SetLongLinkWait(true);
                    }
                    await Task.Delay(1500, ct);
                }

                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetFailure(FailureKind.LinkCheckFailed, ex.Message);
                throw;
            }
        }

        internal static async Task RunLinkThenConfigureAsync(
            Func<CancellationToken, Task> waitForLink,
            Func<CancellationToken, Task> configureAdapter,
            CancellationToken cancellationToken)
        {
            await waitForLink(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await configureAdapter(cancellationToken);
        }

        private async Task<DhcpLease> WaitForLeaseAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Log("DHCP lease wait started");
            SetWorkflowState(DiscoveryWorkflowState.WaitingForDhcp);
            StartDhcpElapsedTimer();
            StatusText = "\u6b63\u5728\u7b49\u5019 BMC \u4e0a\u7ebf";
            ActivityText = "等待 IPMI/BMC 通过 DHCP 获取地址，最多 3 分钟；如未完成，请检查固定 IP、防火墙和管理口连接。";

            var tcs = new TaskCompletionSource<DhcpLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object s, DhcpLease l) => tcs.TrySetResult(l);
            void ErrorHandler(object s, string msg) => tcs.TrySetException(new InvalidOperationException(msg));
            if (_dhcpServer != null) _dhcpServer.LeaseAssigned += Handler;
            if (_dhcpServer != null) _dhcpServer.ErrorEncountered += ErrorHandler;
            if (_dhcpServer != null && _dhcpServer.LastAssignedLease != null)
            {
                Log("DHCP lease was assigned before lease wait started; using cached lease: " +
                    _dhcpServer.LastAssignedLease.IpAddress);
                tcs.TrySetResult(_dhcpServer.LastAssignedLease);
            }

            try
            {
                if (!string.IsNullOrEmpty(_dhcpServerError))
                    throw new InvalidOperationException(_dhcpServerError);

                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
                using (var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
                {
                    var lease = await tcs.Task;
                    ct.ThrowIfCancellationRequested();
                    Log("DHCP candidate acquired: IP=" + lease.IpAddress + " MAC=" + MacBytesToString(lease.MacAddress));
                    RecordDhcpAck(lease);
                    return lease;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log("DHCP lease wait timed out");
                var adapter = _selectedAdapter ?? SelectedAdapterItem;
                FirewallAssessment firewallAssessment;
                if (adapter == null)
                {
                    firewallAssessment = FirewallAssessmentService.CreateUnknown(
                        FirewallAssessmentService.GetCurrentExecutablePath(),
                        "No selected adapter was available for the timeout assessment.");
                }
                else
                {
                    firewallAssessment = await AssessFirewallAsync(adapter, ct);
                }
                _lastDhcpWaitTimedOut = true;
                _lastTimeoutFirewallRisk = firewallAssessment.RiskLevel;
                SetCurrentFirewallAssessment(firewallAssessment);
                SetDhcpElapsedToMaximum();
                SetFailure(FailureKind.DhcpTimedOut, firewallAssessment.BuildTimeoutGuidance());
                throw new TimeoutException(firewallAssessment.BuildTimeoutGuidance());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SetFailure(FailureKind.DhcpListenerFailed, ex.Message);
                throw;
            }
            finally
            {
                if (_dhcpServer != null) _dhcpServer.LeaseAssigned -= Handler;
                if (_dhcpServer != null) _dhcpServer.ErrorEncountered -= ErrorHandler;
            }
        }

        private void SetEndpointProbeOutcome(BmcEndpointProbeOutcome outcome)
        {
            if (outcome == null)
                _endpointAdapterResolutionDiagnostic = string.Empty;
            _lastReachabilityResult = null;
            _reachabilityDiagnostic = string.Empty;
            _lastEndpointProbeEvidence = outcome == null ? null : outcome.Evidence;
            _lastEndpointProbeAttempts = outcome == null ? 0 : outcome.Attempts;
            _endpointNetworkWarning = outcome == null || outcome.VerifiedEndpoint == null
                ? string.Empty
                : EndpointNetworkEnvironment.GetPotentialInterferenceWarning();
            OnPropertyChanged(nameof(EndpointVerificationText));
            OnPropertyChanged(nameof(EndpointVerificationDiagnosticText));
            OnPropertyChanged(nameof(EndpointProtocolPortText));
            OnPropertyChanged(nameof(EndpointNetworkWarning));
            OnPropertyChanged(nameof(HasEndpointNetworkWarning));
        }

        private void SetReachabilityOutcome(BmcReachabilityResult result)
        {
            _lastReachabilityResult = result;
            _lastEndpointProbeEvidence = null;
            _lastEndpointProbeAttempts = 0;
            _endpointAdapterResolutionDiagnostic = string.Empty;
            _endpointNetworkWarning = string.Empty;
            _reachabilityDiagnostic = result == null
                ? string.Empty
                : "target=" + result.TargetAddress +
                  "; ping=" + result.PingSucceeded +
                  "; tcp443=" + result.HttpsPortOpen +
                  "; tcp80=" + result.HttpPortOpen +
                  "; elapsedMs=" + result.Elapsed.TotalMilliseconds.ToString("0") +
                  (string.IsNullOrWhiteSpace(result.FailureDetail) ? string.Empty : "; detail=" + result.FailureDetail);
            OnPropertyChanged(nameof(EndpointVerificationText));
            OnPropertyChanged(nameof(EndpointVerificationDiagnosticText));
            OnPropertyChanged(nameof(EndpointProtocolPortText));
            OnPropertyChanged(nameof(EndpointNetworkWarning));
            OnPropertyChanged(nameof(HasEndpointNetworkWarning));
            OnPropertyChanged(nameof(LegacyRuntime));
        }

        private async Task<BmcEndpointProbeRequest> CreateEndpointProbeRequestAsync(
            IPAddress targetAddress,
            CancellationToken cancellationToken)
        {
            var adapter = _selectedAdapter ?? SelectedAdapterItem;
            if (adapter == null)
                throw new InvalidOperationException("未确认用于直连 BMC 的网卡。");

            IPAddress sourceAddress;
            if (!IPAddress.TryParse(_subnetConfig.ServerIp, out sourceAddress) ||
                sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("当前临时本机 IPv4 地址无效。");

            var resolution = await AdapterIdentityResolver.ResolveAsync(
                adapter,
                sourceAddress,
                cancellationToken,
                message => Log("[AdapterResolve] " + message));
            _endpointAdapterResolutionDiagnostic = resolution.Diagnostic + "; attempts=" + resolution.Attempts;
            OnPropertyChanged(nameof(EndpointVerificationDiagnosticText));
            if (!resolution.Success)
                throw new EndpointAdapterUnavailableException(resolution);

            var networkInterface = resolution.NetworkInterface;
            var interfaceIndex = resolution.Candidate.InterfaceIndex;
            if (interfaceIndex <= 0)
                throw new InvalidOperationException("无法读取所选网卡的 IPv4 接口索引。");

            var lease = _dhcpServer == null ? null : _dhcpServer.LastAssignedLease;
            var leaseMac = lease == null ? null : lease.MacAddress;
            return new BmcEndpointProbeRequest
            {
                TargetAddress = targetAddress,
                SourceAddress = sourceAddress,
                AdapterName = networkInterface.Name,
                AdapterId = networkInterface.Id,
                InterfaceIndex = interfaceIndex,
                ExpectedPeerMac = leaseMac != null && leaseMac.Length > 0
                    ? BitConverter.ToString(leaseMac)
                : string.Empty
            };
        }

        private async Task<BmcReachabilityResult> ProbeReachabilityAsync(
            IPAddress targetAddress,
            CancellationToken cancellationToken)
        {
            IPAddress sourceAddress;
            if (!IPAddress.TryParse(_subnetConfig.ServerIp, out sourceAddress) ||
                sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("当前临时本机 IPv4 地址无效。");

            var probe = ReachabilityProbeForTests;
            if (probe != null)
                return await probe(targetAddress, sourceAddress, cancellationToken);

            return await BmcReachabilityProbe.ProbeAsync(
                targetAddress,
                sourceAddress,
                cancellationToken,
                message => Log("[Reachability] " + message),
                BmcReachabilityProbe.DefaultTimeout);
        }

        private async Task<BmcDiscoveryResult> DiscoverBmcAddressAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            IPAddress configuredAddress;
            if (!IPAddress.TryParse(_subnetConfig.PoolStart, out configuredAddress))
            {
                var lease = await WaitForLeaseAsync(ct);
                return new BmcDiscoveryResult
                {
                    IpAddress = lease.IpAddress,
                    Source = BmcDiscoverySource.Dhcp
                };
            }

            var discovery = await BmcDiscovery.WaitForAddressAsync(
                configuredAddress,
                async token =>
                {
                    var lease = await WaitForLeaseAsync(token);
                    return lease.IpAddress;
                },
                async (address, token) =>
                {
                    try
                    {
                        var reachability = await ProbeReachabilityAsync(address, token);
                        SetReachabilityOutcome(reachability);
                        return reachability;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Retained-address reachability is a convenience race.
                        // Its failure must leave the real DHCP wait running.
                        Log("Configured-address reachability unavailable: " + ex.Message);
                        return null;
                    }
                },
                ct);
            ct.ThrowIfCancellationRequested();

            if (discovery.Source == BmcDiscoverySource.ExistingConfiguredAddress)
            {
                SetCandidateAddress(
                    discovery.IpAddress.ToString(),
                    CandidateAddressSource.ExistingConfiguredAddress);
                Log("BMC address reachable at configured address " + discovery.IpAddress +
                    "; retained address path carried lightweight reachability evidence and ended DHCP waiting.");
            }
            else if (!SessionState.HasCandidateAddress ||
                     !string.Equals(SessionState.CandidateAddress.IPv4Address, discovery.IpAddress.ToString(), StringComparison.Ordinal))
            {
                SetCandidateAddress(discovery.IpAddress.ToString(), CandidateAddressSource.DhcpAck);
            }

            return discovery;
        }

        private static string MacBytesToString(byte[] mac)
        {
            if (mac == null || mac.Length == 0)
                return "none";
            return BitConverter.ToString(mac);
        }

        private async Task<FirewallAssessment> AssessFirewallAsync(
            WiredAdapter adapter,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var assessment = await FirewallAssessmentService.AssessAsync(
                adapter.Name,
                adapter.Id,
                adapter.MacAddress,
                FirewallAssessmentService.GetCurrentExecutablePath(),
                _firewallNetworkCategorySnapshot,
                cancellationToken);
            Log("Firewall assessment: adapter=" + adapter.Name +
                " interfaceIndex=" + (assessment.InterfaceIndex.HasValue ? assessment.InterfaceIndex.Value.ToString() : "unknown") +
                " category=" + assessment.NetworkCategory +
                " observedCategory=" + assessment.ObservedNetworkCategory +
                " categorySource=" + assessment.NetworkCategorySource +
                " enabled=" + (assessment.SelectedFirewallEnabled.HasValue ? assessment.SelectedFirewallEnabled.Value.ToString() : "unknown") +
                " risk=" + assessment.RiskLevel +
                " appAllow=" + assessment.HasMatchingProgramAllow +
                " appBlock=" + assessment.HasMatchingProgramBlock +
                " portAllow=" + assessment.HasMatchingPortAllow +
                " portBlock=" + assessment.HasMatchingPortBlock +
                (string.IsNullOrWhiteSpace(assessment.Error) ? "" : " error=" + assessment.Error));
            return assessment;
        }

        private void CaptureFirewallNetworkCategorySnapshot(FirewallAssessment assessment)
        {
            if (assessment == null || assessment.UsesNetworkCategorySnapshot ||
                string.Equals(assessment.NetworkCategory, "Unknown", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(assessment.NetworkCategorySource, "Selected adapter (Get-NetConnectionProfile)", StringComparison.Ordinal))
                return;

            _firewallNetworkCategorySnapshot = assessment.NetworkCategory;
            Log("Firewall profile snapshot captured before adapter configuration: " +
                _firewallNetworkCategorySnapshot);
        }

        private async Task RetryEndpointProbeAsync()
        {
            if (IsFirewallRepairBusy) return;
            IPAddress ipAddress;
            if (IsEndpointProbeRunning || !IPAddress.TryParse(SessionState.CandidateAddress == null ? null : SessionState.CandidateAddress.IPv4Address, out ipAddress))
                return;
            try
            {
                var token = _flowCts?.Token ?? CancellationToken.None;
                await ProbeBmcEndpointAsync(ipAddress, true, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log("BMC endpoint retry failed: " + ex.Message);
                EndpointStatusText = "重新检测地址可达性时遇到问题：" + ex.Message;
            }
        }

        private async Task<bool> ProbeBmcEndpointAsync(IPAddress ipAddress, bool autoOpen, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsEndpointProbeRunning = true;
            BeginEndpointProbe();
            EndpointStatusText = "已取得候选管理地址，正在进行 Ping 和 TCP 80/443 可达性检测...";
            StatusText = "已取得候选管理地址，正在确认可达性…";
            DetailText = "地址：" + ipAddress + "；最长检测 5 秒，不读取网页内容。";
            ActivityText = "正在并行检测 Ping、TCP 443 和 TCP 80。";
            BadgeText = "处理中";
            BadgeColor = "#0078D4";

            try
            {
                var reachability = await ProbeReachabilityAsync(ipAddress, cancellationToken);
                SetReachabilityOutcome(reachability);
                cancellationToken.ThrowIfCancellationRequested();
                return await PresentReachabilityAsync(ipAddress, reachability, autoOpen, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FailSession(FailureKind.EndpointProbeError, ex.Message);
                Log("BMC reachability probe failed: " + ex.Message);
                throw;
            }
            finally
            {
                IsEndpointProbeRunning = false;
            }
        }

        private Task<bool> PresentReachabilityAsync(
            IPAddress ipAddress,
            BmcReachabilityResult reachability,
            bool autoOpen,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reachability == null)
                return Task.FromResult(false);

            if (!reachability.IsReachable)
            {
                SetWorkflowState(DiscoveryWorkflowState.EndpointUnreachable);
                PreferredBmcScheme = "https";
                EndpointStatusText = "候选管理地址已保留，但暂未确认可达。";
                StatusText = "已获取候选地址，尚未确认可达";
                DetailText = "5 秒内未收到 Ping 或 TCP 80/443 的成功响应。地址仍会保留；可以重新检测，或手动尝试 HTTPS / HTTP。网页内容和状态码不会影响地址分配结果。";
                ActivityText = "候选地址已保留，等待用户重新检测或手动访问。";
                BadgeText = "等待确认";
                BadgeColor = "#8A6D1D";
                return Task.FromResult(false);
            }

            PreferredBmcScheme = reachability.PreferredScheme;
            SetWorkflowState(DiscoveryWorkflowState.EndpointReachable);
            var transport = reachability.HttpsPortOpen && reachability.HttpPortOpen
                ? "TCP 443、80"
                : reachability.HttpsPortOpen
                    ? "TCP 443"
                    : reachability.HttpPortOpen ? "TCP 80" : "Ping";
            EndpointStatusText = "地址已确认可达（" + transport + "）。";
            Log("BMC address reachable: ip=" + ipAddress +
                " ping=" + reachability.PingSucceeded +
                " tcp443=" + reachability.HttpsPortOpen +
                " tcp80=" + reachability.HttpPortOpen);
            RememberReachableBmc(ipAddress, reachability);
            if (autoOpen && !string.IsNullOrWhiteSpace(reachability.PreferredUrl))
                OpenBrowser(reachability.PreferredUrl);
            StatusText = "地址已分配并确认可达";
            var icmpDetail = reachability.PingSucceeded
                ? string.Empty
                : "设备未响应 ICMP Ping，但 TCP 握手成功，仍判定地址可达。 ";
            var browserDetail = string.IsNullOrWhiteSpace(reachability.PreferredUrl)
                ? icmpDetail + "80/443 暂无可连接端口；网页服务可能未启动或使用其他端口。可以稍后重新检测或手动尝试 HTTPS / HTTP。"
                : SessionState.BrowserLaunchResult == BrowserLaunchResult.RequestFailed
                    ? icmpDetail + "已确认地址可达，但浏览器启动请求失败；可以复制地址或手动打开。"
                    : icmpDetail + "已确认地址可达，已请求浏览器打开 " + reachability.PreferredUrl + "。网页是否最终加载以浏览器实际显示为准。";
            DetailText = browserDetail + " 完成后点击退出并恢复网卡。";
            BadgeText = "已完成";
            BadgeColor = "#107C10";
            ActivityText = "地址已分配并确认可达；完成浏览器中的操作后恢复网卡。";
            return Task.FromResult(true);
        }

        private void OpenBrowserForScheme(string scheme)
        {
            var candidateAddress = SessionState.CandidateAddress == null
                ? null
                : SessionState.CandidateAddress.IPv4Address;
            if (!string.IsNullOrEmpty(candidateAddress))
                OpenBrowser(scheme + "://" + candidateAddress);
        }

        private void OpenBrowser(string url)
        {
            var handler = OpenBrowserRequested;
            if (handler == null)
            {
                SetBrowserLaunchResult(BrowserLaunchResult.RequestFailed);
                Log("Browser open failed: no browser-launch handler is registered.");
                return;
            }

            try
            {
                handler.Invoke(url);
                SetBrowserLaunchResult(BrowserLaunchResult.Requested);
                Log("Browser open requested for " + url);
            }
            catch (Exception ex)
            {
                SetBrowserLaunchResult(BrowserLaunchResult.RequestFailed);
                Log("Browser open failed: " + ex.Message);
            }
        }

        private void CopyIp()
        {
            if (string.IsNullOrWhiteSpace(DiscoveredIpUrl))
                return;

            try
            {
                Clipboard.SetText(DiscoveredIpUrl);
                Log("Candidate management URL copied to clipboard.");
            }
            catch (Exception ex)
            {
                Log("Candidate management URL copy failed: " + ex.Message);
            }
        }

        private void OfferHistoryRetryIfEligible()
        {
            var history = _selectedAdapter == null ? null : BmcHistoryStore.LoadForAdapter(_selectedAdapter);
            if (!BmcHistoryRetryPolicy.ShouldOffer(
                    _selectedAdapter, _subnetConfig, history, _historyRetryUsed, _lastTimeoutFirewallRisk))
                return;

            SetHistoryRetrySuggestion(history);
            Log("History retry offered: adapter=" + _selectedAdapter.Name +
                " previousBmc=" + history.BmcAddress + " previousLocal=" + history.LocalAddress);
        }

        private void SetHistoryRetrySuggestion(BmcHistoryRecord history)
        {
            _historyRetrySuggestion = history;
            OnPropertyChanged(nameof(ShowHistoryRetrySuggestion));
            OnPropertyChanged(nameof(HistoryRetrySuggestionText));
            OnPropertyChanged(nameof(HistoryAdapterName));
            OnPropertyChanged(nameof(HistoryLocalAddress));
            OnPropertyChanged(nameof(HistoryBmcAddress));
            OnPropertyChanged(nameof(HistoryEndpointText));
            OnPropertyChanged(nameof(HistoryLastConfirmedText));
            OnPropertyChanged(nameof(ShowStandaloneSupportBundleAction));
            NotifySessionPresentationChanged();
        }

        private async Task PrepareHistoryRetryAsync()
        {
            if (IsFirewallRepairBusy) return;
            var history = _historyRetrySuggestion;
            var adapter = _selectedAdapter ?? SelectedAdapterItem;
            if (history == null || adapter == null || _isCleaningUp || _isClosing)
                return;

            if (!history.TryApplyTo(new SubnetConfig()))
            {
                SetHistoryRetrySuggestion(null);
                return;
            }

            if (!RequestConsent(ConsentNotice.CreateHistoryRetryPreparation(adapter, history)))
            {
                Log("History retry preparation declined");
                return;
            }

            _historyRetryUsed = true;
            SetHistoryRetrySuggestion(null);
            ShowSupportBundleAction = false;
            _isCleaningUp = true;
            Log("History retry preparation accepted: adapter=" + adapter.Name +
                " targetLocal=" + history.LocalAddress + " targetBmc=" + history.BmcAddress);

            try
            {
                StatusText = "正在恢复网卡并准备上次网段...";
                ActivityText = "请稍候，当前 DHCP 服务将停止，本机网卡会先恢复。";
                BadgeText = "处理中";
                BadgeColor = "#0078D4";
                await DoCleanupAsync();

                // Cleanup succeeded and the user is returning to adapter
                // selection, not continuing to view the old timeout page.
                // Reset the shared session state before pre-filling history.
                ResetSessionStateForNewFlow();
                SetEndpointProbeOutcome(null);

                if (!history.TryApplyTo(_subnetConfig))
                {
                    StatusText = "无法填入上次网段";
                    DetailText = "网卡已恢复，但保存的历史地址无效。请手动选择网段后重新开始。";
                    BadgeText = "历史无效";
                    BadgeColor = "#D13438";
                    ShowSupportBundleAction = true;
                    return;
                }

                _isFlowStarted = false;
                _isCleanupDone = false;
                _dhcpServerError = null;
                _lastDhcpWaitTimedOut = false;
                _lastTimeoutFirewallRisk = null;
                _adapterSelectionEnabled = true;
                _startButtonEnabled = true;
                StatusText = "已恢复网卡并填入上次网段";
                DetailText = "已填入 " + _subnetConfig.ServerDisplay +
                    "；请确认网卡后手动点击“开始”，并再次确认网络变更。";
                ActivityText = string.Empty;
                BadgeText = "等待重新开始";
                BadgeColor = "#666";
                OnPropertyChanged(nameof(AdapterSelectionEnabled));
                OnPropertyChanged(nameof(StartButtonEnabled));
                OnPropertyChanged(nameof(ShowAdapterSelectionContent));
                OnPropertyChanged(nameof(ShowLegacyHeader));
                OnPropertyChanged(nameof(ShowLegacyRuntimeHost));
                OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
                OnPropertyChanged(nameof(ShowLegacyRuntimeCard));
                Log("History retry preparation completed; returned to adapter selection with " + _subnetConfig.ServerDisplay);
            }
            catch (Exception ex)
            {
                Log("History retry preparation cleanup failed: " + ex.Message);
                RecordCleanupFailure(ex);
                StatusText = "恢复网卡设置时遇到问题";
                DetailText = ex.Message;
                BadgeText = "失败";
                BadgeColor = "#D13438";
                ShowSupportBundleAction = true;
            }
            finally
            {
                _isCleaningUp = false;
            }
        }

        private void RememberReachableBmc(IPAddress bmcAddress, BmcReachabilityResult reachability)
        {
            if (_selectedAdapter == null)
                return;

            try
            {
                var lease = _dhcpServer == null ? null : _dhcpServer.LastAssignedLease;
                var bmcMac = lease == null || lease.MacAddress == null || lease.MacAddress.Length == 0
                    ? string.Empty
                    : BitConverter.ToString(lease.MacAddress);
                if (string.IsNullOrWhiteSpace(bmcMac))
                {
                    Log("BMC history skipped: DHCP client MAC was unavailable.");
                    return;
                }
                BmcHistoryStore.SaveReachableAddress(_selectedAdapter, _subnetConfig, bmcAddress, reachability, bmcMac);
                Log("BMC history updated: adapter=" + _selectedAdapter.Name +
                    " bmc=" + bmcAddress + " local=" + _subnetConfig.ServerDisplay);
            }
            catch (Exception ex)
            {
                Log("BMC history update failed: " + ex.Message);
            }
        }

        private void ResetSessionStateForNewFlow()
        {
            StopDhcpElapsedTimer(true);
            ResetSessionStateForNewFlow(SessionState);
            NotifySessionStateChanged();
        }

        internal static void ResetSessionStateForNewFlow(CurrentSessionState sessionState)
        {
            if (sessionState == null) throw new ArgumentNullException(nameof(sessionState));
            sessionState.Workflow = DiscoveryWorkflowState.Ready;
            sessionState.Network = NetworkLifecycleState.Untouched;
            sessionState.DhcpEvidence = DhcpEvidenceState.None;
            sessionState.BrowserLaunchResult = BrowserLaunchResult.NotRequested;
            sessionState.ClearCandidateAddress();
            sessionState.ClearFailure();
        }

        private void MarkFlowCancelled()
        {
            StopDhcpElapsedTimer(true);
            ApplyFlowCancellation(SessionState);
            NotifySessionStateChanged();
        }

        internal static void ApplyFlowCancellation(CurrentSessionState sessionState)
        {
            if (sessionState == null) throw new ArgumentNullException(nameof(sessionState));
            sessionState.ClearFailure();
            sessionState.Workflow = DiscoveryWorkflowState.Cancelled;
        }

        private static bool IsWorkflowRunning(DiscoveryWorkflowState workflow)
        {
            return workflow == DiscoveryWorkflowState.WaitingForLink
                || workflow == DiscoveryWorkflowState.ConfiguringNetwork
                || workflow == DiscoveryWorkflowState.WaitingForDhcp
                || workflow == DiscoveryWorkflowState.ProbingEndpoint;
        }

        private void SetWorkflowState(DiscoveryWorkflowState workflow)
        {
            if (SessionState.Workflow == workflow) return;
            if (workflow != DiscoveryWorkflowState.WaitingForLink)
                SetLongLinkWait(false);
            if (workflow != DiscoveryWorkflowState.WaitingForDhcp)
                StopDhcpElapsedTimer(workflow != DiscoveryWorkflowState.Failed);
            SessionState.Workflow = workflow;
            NotifySessionStateChanged();
        }

        private void SetNetworkState(NetworkLifecycleState network)
        {
            if (SessionState.Network == network) return;
            SessionState.Network = network;
            NotifySessionStateChanged();
        }

        // SessionState is projected after each mutation. Keep the ACK evidence and its
        // assigned candidate together so the UI never observes AckSent without an address.
        private void RecordDhcpAck(DhcpLease lease)
        {
            SessionState.SetCandidateAddress(lease.IpAddress.ToString(), CandidateAddressSource.DhcpAck);
            SessionState.DhcpEvidence = DhcpEvidenceState.AckSent;
            NotifySessionStateChanged();
        }

        private void SetCandidateAddress(string ipv4Address, CandidateAddressSource source)
        {
            SessionState.SetCandidateAddress(ipv4Address, source);
            NotifySessionStateChanged();
        }

        private void ClearCandidateAddress()
        {
            if (!SessionState.HasCandidateAddress) return;
            SessionState.ClearCandidateAddress();
            NotifySessionStateChanged();
        }

        private void SetFailure(FailureKind kind, string detail)
        {
            SessionState.SetFailure(kind, detail);
            NotifySessionStateChanged();
        }

        // A retry clears its former probe exception and becomes ProbingEndpoint in the
        // same observable update; consumers must not see Failed with no Failure.
        private void BeginEndpointProbe()
        {
            StopDhcpElapsedTimer(true);
            if (SessionState.Failure != null
                && (SessionState.Failure.Kind == FailureKind.EndpointProbeError
                    || SessionState.Failure.Kind == FailureKind.EndpointAdapterUnavailable))
                SessionState.ClearFailure();
            SessionState.Workflow = DiscoveryWorkflowState.ProbingEndpoint;
            NotifySessionStateChanged();
        }

        private void FailSession(FailureKind kind, string detail)
        {
            SetFailure(kind, detail);
            SetWorkflowState(DiscoveryWorkflowState.Failed);
        }

        private void SetBrowserLaunchResult(BrowserLaunchResult result)
        {
            if (SessionState.BrowserLaunchResult == result) return;
            SessionState.BrowserLaunchResult = result;
            NotifySessionStateChanged();
        }

        private bool UsesSessionPresentation =>
            SessionState.Network == NetworkLifecycleState.Restoring
            || SessionState.Network == NetworkLifecycleState.RestoreFailed
            || SessionState.Workflow != DiscoveryWorkflowState.Ready;

        private string CurrentAdapterDisplayName =>
            _selectedAdapter?.DisplayName
            ?? SelectedAdapterItem?.DisplayName
            ?? _recoverySnapshot?.AdapterName
            ?? string.Empty;

        private void SetCurrentFirewallAssessment(FirewallAssessment assessment)
        {
            _firewallTechnicalDetail = assessment.ToDiagnosticText();
            OnPropertyChanged(nameof(FirewallTechnicalDetail));
            OnPropertyChanged(nameof(HasFirewallTechnicalDetail));
            SetCurrentFirewallRisk(assessment.RiskLevel);
        }

        private void ClearCurrentFirewallAssessment()
        {
            _firewallTechnicalDetail = string.Empty;
            OnPropertyChanged(nameof(FirewallTechnicalDetail));
            OnPropertyChanged(nameof(HasFirewallTechnicalDetail));
            SetCurrentFirewallRisk(null);
        }

        private void SetCurrentFirewallRisk(FirewallRiskLevel? riskLevel)
        {
            if (_currentFirewallRisk == riskLevel)
                return;

            _currentFirewallRisk = riskLevel;
            OnPropertyChanged(nameof(ShowFirewallRepair));
            OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
            OnPropertyChanged(nameof(ShowAdapterFirewallRetry));
            OnPropertyChanged(nameof(AdapterFirewallNoticeTitle));
            OnPropertyChanged(nameof(AdapterFirewallNoticeSeverity));
            OnPropertyChanged(nameof(FirewallSummary));
            OnPropertyChanged(nameof(FirewallSeverity));
            OnPropertyChanged(nameof(HasFirewallAssessment));
            OnPropertyChanged(nameof(DhcpTimeoutFollowUpText));
            OnPropertyChanged(nameof(HasDhcpTimeoutFollowUp));
            OnPropertyChanged(nameof(ShowDhcpFirewallEvidence));
        }

        private void SetLongLinkWait(bool value)
        {
            if (_hasLongLinkWait == value) return;
            _hasLongLinkWait = value;
            OnPropertyChanged(nameof(LinkStatusText));
            OnPropertyChanged(nameof(ShowLongLinkWaitHint));
            OnPropertyChanged(nameof(LegacyRuntime));
        }

        // Presentation-only timer. The three-minute DHCP timeout remains owned by
        // WaitForLeaseAsync and is not derived from this display value.
        private void StartDhcpElapsedTimer()
        {
            StopDhcpElapsedTimer(true);
            _dhcpWaitStopwatch = Stopwatch.StartNew();
            _dhcpWaitStartedUtc = DateTime.UtcNow;
            _lastDhcpTimerTickUtc = DateTime.UtcNow;
            _dhcpTimerLagLogged = false;
            UpdateDhcpElapsedDisplay();
            _dhcpElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _dhcpElapsedTimer.Tick += OnDhcpElapsedTimerTick;
            _dhcpElapsedTimer.Start();
        }

        private void OnDhcpElapsedTimerTick(object sender, EventArgs e)
        {
            UpdateDhcpElapsedDisplay();
        }

        private void StopDhcpElapsedTimer(bool reset)
        {
            if (_dhcpElapsedTimer != null)
            {
                _dhcpElapsedTimer.Tick -= OnDhcpElapsedTimerTick;
                _dhcpElapsedTimer.Stop();
                _dhcpElapsedTimer = null;
            }

            _dhcpWaitStartedUtc = null;
            _dhcpWaitStopwatch = null;
            _lastDhcpTimerTickUtc = null;
            _dhcpTimerLagLogged = false;
            if (_hasLongDhcpWait)
            {
                _hasLongDhcpWait = false;
                OnPropertyChanged(nameof(ShowLongDhcpWaitHint));
                OnPropertyChanged(nameof(ShowDhcpFirewallEvidence));
            }

            if (reset)
                DhcpElapsedText = "00:00";
        }

        private void SetDhcpElapsedToMaximum()
        {
            DhcpElapsedText = "03:00";
        }

        private void UpdateDhcpElapsedDisplay()
        {
            if (!_dhcpWaitStartedUtc.HasValue)
                return;

            var elapsed = _dhcpWaitStopwatch != null
                ? _dhcpWaitStopwatch.Elapsed
                : DateTime.UtcNow - _dhcpWaitStartedUtc.Value;
            var now = DateTime.UtcNow;
            if (_lastDhcpTimerTickUtc.HasValue)
            {
                var gap = now - _lastDhcpTimerTickUtc.Value;
                if (gap >= TimeSpan.FromSeconds(2.5) && !_dhcpTimerLagLogged)
                {
                    _dhcpTimerLagLogged = true;
                    Log("DHCP UI timer delayed: gapMs=" + gap.TotalMilliseconds.ToString("0"));
                }
            }
            _lastDhcpTimerTickUtc = now;
            DhcpElapsedText = FormatDhcpElapsed(elapsed);
            var isLongWait = elapsed >= TimeSpan.FromSeconds(60);
            if (_hasLongDhcpWait == isLongWait)
                return;

            _hasLongDhcpWait = isLongWait;
            OnPropertyChanged(nameof(ShowLongDhcpWaitHint));
            OnPropertyChanged(nameof(ShowDhcpFirewallEvidence));
            OnPropertyChanged(nameof(LegacyRuntime));
        }

        internal static string FormatDhcpElapsed(TimeSpan elapsed)
        {
            var totalSeconds = (int)Math.Floor(Math.Max(0, Math.Min(180, elapsed.TotalSeconds)));
            return (totalSeconds / 60).ToString("00") + ":" + (totalSeconds % 60).ToString("00");
        }

        private void NotifySessionPresentationChanged()
        {
            OnPropertyChanged(nameof(LegacyRuntime));
            OnPropertyChanged(nameof(CurrentSessionPresentation));
            OnPropertyChanged(nameof(CurrentSessionPage));
            OnPropertyChanged(nameof(SessionPageTitle));
            OnPropertyChanged(nameof(SessionPageSummary));
            OnPropertyChanged(nameof(RecommendedActionKind));
            OnPropertyChanged(nameof(NetworkStatusPresentation));
            OnPropertyChanged(nameof(NetworkStatusText));
            OnPropertyChanged(nameof(NetworkStatusSeverity));
            OnPropertyChanged(nameof(CandidateSourceText));
            OnPropertyChanged(nameof(BrowserStatusText));
            OnPropertyChanged(nameof(BrowserStatusSeverity));
            OnPropertyChanged(nameof(EndpointProtocolPortText));
            OnPropertyChanged(nameof(FirewallSummary));
            OnPropertyChanged(nameof(FirewallSeverity));
            OnPropertyChanged(nameof(HasFirewallAssessment));
            OnPropertyChanged(nameof(FirewallTechnicalDetail));
            OnPropertyChanged(nameof(HasFirewallTechnicalDetail));
            OnPropertyChanged(nameof(CurrentAdapterName));
            OnPropertyChanged(nameof(LinkStatusText));
            OnPropertyChanged(nameof(ShowLongLinkWaitHint));
            OnPropertyChanged(nameof(DhcpElapsedText));
            OnPropertyChanged(nameof(ShowLongDhcpWaitHint));
            OnPropertyChanged(nameof(ShowDhcpFirewallEvidence));
            OnPropertyChanged(nameof(DhcpEvidenceText));
            OnPropertyChanged(nameof(DhcpEvidenceSeverity));
            OnPropertyChanged(nameof(DhcpTimeoutFollowUpText));
            OnPropertyChanged(nameof(HasDhcpTimeoutFollowUp));
            OnPropertyChanged(nameof(FailurePresentation));
            OnPropertyChanged(nameof(FailureTitle));
            OnPropertyChanged(nameof(FailureSummary));
            OnPropertyChanged(nameof(FailureRecommendedActionKind));
            OnPropertyChanged(nameof(FailureRecommendedActionText));
            OnPropertyChanged(nameof(FailureNetworkImpactText));
            OnPropertyChanged(nameof(CanRetryEndpointFromFailure));
            OnPropertyChanged(nameof(FailureTechnicalDetail));
            OnPropertyChanged(nameof(HasFailureTechnicalDetail));
            OnPropertyChanged(nameof(RecoveryAdapterDisplayName));
            OnPropertyChanged(nameof(RecoveryOriginalConfigDetails));
            OnPropertyChanged(nameof(ShowLegacyHeader));
            OnPropertyChanged(nameof(ShowPreparationContent));
            OnPropertyChanged(nameof(ShowAdapterSelectionContent));
            OnPropertyChanged(nameof(ShowLegacyRuntimeCard));
            OnPropertyChanged(nameof(ShowLegacyRuntimeHost));
            OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(ExitButtonText));
            OnPropertyChanged(nameof(IsExitActionEnabled));
        }

        private void NotifySessionStateChanged()
        {
            OnPropertyChanged(nameof(FirewallRepairEnabled));
            OnPropertyChanged(nameof(ShowFirewallRepair));
            OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
            OnPropertyChanged(nameof(SessionState));
            OnPropertyChanged(nameof(DiscoveredIp));
            OnPropertyChanged(nameof(IsIpDiscovered));
            OnPropertyChanged(nameof(DiscoveredIpUrl));
            NotifySessionPresentationChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task<bool> WaitForPendingRecoveryBeforeCleanupAsync()
        {
            var pendingRecoveryTask = _pendingRecoveryTask;
            if (SessionState.Network != NetworkLifecycleState.Restoring
                || pendingRecoveryTask == null
                || pendingRecoveryTask.IsCompleted)
                return true;

            Log("Cleanup: waiting for pending startup recovery instead of starting a second restore");
            try
            {
                await pendingRecoveryTask;
                return true;
            }
            catch (Exception ex)
            {
                Log("Cleanup: pending startup recovery failed: " + ex.Message);
                return false;
            }
        }

        private void RecordCleanupFailure(Exception ex)
        {
            var restoreFailed = SessionState.RequiresNetworkRecovery
                || SessionState.Network == NetworkLifecycleState.Restoring;
            if (restoreFailed)
            {
                SetNetworkState(NetworkLifecycleState.RestoreFailed);
                SetFailure(FailureKind.RestoreFailed, ex.Message);
            }
            else if (!SessionState.HasFailure)
            {
                SetFailure(FailureKind.Unexpected, ex.Message);
            }
        }

        private bool RequestConsent(ConsentNotice notice)
        {
            var presenter = ConsentRequested;
            if (presenter == null)
            {
                Log("Consent blocked because no presenter is registered: " + notice.Title);
                return false;
            }

            try
            {
                return presenter.Invoke(notice);
            }
            catch (Exception ex)
            {
                Log("Consent dialog failed: " + ex.Message);
                StatusText = "无法显示风险告知";
                DetailText = "为避免误操作，已停止继续：" + ex.Message;
                BadgeText = "无法确认";
                BadgeColor = "#D13438";
                return false;
            }
        }

        public async Task<bool> CleanupAsync()
        {
            if (IsFirewallRepairBusy) return false;
            if (_isClosing || _isCleaningUp) return false;
            if (IsStartPreflightBusy)
                _startPreflightCts?.Cancel();
            _isClosing = true;
            _isCleaningUp = true;
            Log("Cleanup started");
            try
            {
                if (!await WaitForPendingRecoveryBeforeCleanupAsync())
                {
                    _isCleaningUp = false;
                    _isClosing = false;
                    return false;
                }

                StatusText = SessionState.RequiresNetworkRecovery
                    ? "\u6b63\u5728\u9000\u51fa\u5e76\u6062\u590d\u7f51\u5361..."
                    : SessionState.RequiresCleanup
                        ? "\u6b63\u5728\u6e05\u7406\u6062\u590d\u4f1a\u8bdd..."
                        : "\u6b63\u5728\u5b89\u5168\u9000\u51fa...";
                ActivityText = "\u8bf7\u7a0d\u5019...";
                BadgeText = "\u5904\u7406\u4e2d";
                BadgeColor = "#0078D4";

                await DoCleanupAsync();

                Log("Cleanup success");
                StatusText = SessionState.Network == NetworkLifecycleState.ConfigurationRestored
                    ? "\u7f51\u5361\u5df2\u6062\u590d"
                    : "\u672a\u4fee\u6539\u7f51\u5361\uff0c\u5df2\u5b89\u5168\u9000\u51fa";
                BadgeText = "\u5df2\u5b8c\u6210";
                BadgeColor = "#107C10";
                _isCleanupDone = true;
                return true;
            }
            catch (Exception ex)
            {
                Log("Cleanup failed: " + ex.Message);
                RecordCleanupFailure(ex);
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
            var networkStateBeforeCleanup = SessionState.Network;
            var requiresNetworkRecovery = SessionState.RequiresNetworkRecovery;
            if (requiresNetworkRecovery)
                SetNetworkState(NetworkLifecycleState.Restoring);

            if (IsWorkflowRunning(SessionState.Workflow))
                MarkFlowCancelled();
            _flowCts?.Cancel();
            try { await _flowTask; } catch (OperationCanceledException) { } catch { }
            try { await _endpointProbeTask; } catch (OperationCanceledException) { } catch { }

            _dhcpServer?.Stop();
            if (_dhcpServer != null)
                _dhcpServer.ErrorEncountered -= OnDhcpServerError;
            _dhcpServer?.Dispose();
            _dhcpServer = null;

            if (requiresNetworkRecovery)
            {
                if (_selectedAdapter == null || _originalConfig == null || _recoverySnapshot == null)
                    throw new InvalidOperationException("\u7f51\u5361\u6062\u590d\u6240\u9700\u7684\u4f1a\u8bdd\u4fe1\u606f\u4e0d\u5b8c\u6574\uff0c\u65e0\u6cd5\u786e\u8ba4\u539f\u59cb\u914d\u7f6e\u5df2\u6062\u590d\u3002");
                var selectedAdapter = _selectedAdapter;
                var originalConfig = _originalConfig;
                var recoverySnapshot = _recoverySnapshot;
                var recoverySubnetConfig = _recoverySubnetConfig ?? _subnetConfig;
                using (var c = new CancellationTokenSource(TimeSpan.FromSeconds(70)))
                {
                    await NetworkRecoveryStore.ExecuteWithRecoveryLockAsync(async () =>
                    {
                        var recoveryAdapter = await Task.Run(
                            () => NetworkConfigManager.ResolveCurrentAdapter(selectedAdapter),
                            c.Token);
                        await NetworkConfigManager.RestoreOriginalConfigAsync(
                            recoveryAdapter, originalConfig, recoverySubnetConfig, c.Token);

                        NetworkRecoveryStore.DeleteIfSessionMatches(recoverySnapshot.SessionId);
                    }, c.Token);
                }

                Log("Cleanup: recovery snapshot removed");
                _recoverySnapshot = null;
                _recoverySubnetConfig = null;
                SetNetworkState(NetworkLifecycleState.ConfigurationRestored);
            }
            else if (networkStateBeforeCleanup == NetworkLifecycleState.RecoveryPrepared)
            {
                if (_recoverySnapshot == null)
                    throw new InvalidOperationException("\u6062\u590d\u4f1a\u8bdd\u5feb\u7167\u4e22\u5931\uff0c\u65e0\u6cd5\u5b89\u5168\u5b8c\u6210\u4f1a\u8bdd\u6e05\u7406\u3002");

                var recoverySnapshot = _recoverySnapshot;
                using (var c = new CancellationTokenSource(TimeSpan.FromSeconds(70)))
                {
                    await NetworkRecoveryStore.ExecuteWithRecoveryLockAsync(() =>
                    {
                        NetworkRecoveryStore.DeleteIfSessionMatches(recoverySnapshot.SessionId);
                        return Task.CompletedTask;
                    }, c.Token);
                }

                _recoverySnapshot = null;
                _recoverySubnetConfig = null;
                SetNetworkState(NetworkLifecycleState.Untouched);
                Log("Cleanup: recovery session removed without network restore");
            }
            else
            {
                Log("Cleanup: no network recovery or session cleanup was required");
            }
        }

        public void CancelFlow()
        {
            if (IsFirewallRepairBusy) return;
            if (_isClosing || _isCleaningUp) return;
            if (IsStartPreflightBusy)
            {
                _startPreflightCts?.Cancel();
                return;
            }
            _isCleaningUp = true;
            MarkFlowCancelled();
            _flowCts?.Cancel();
            Log("Cancel requested");
            StatusText = SessionState.RequiresNetworkRecovery
                ? "\u6b63\u5728\u53d6\u6d88\u5e76\u6062\u590d\u7f51\u5361..."
                : SessionState.RequiresCleanup
                    ? "\u6b63\u5728\u53d6\u6d88\u5e76\u6e05\u7406\u6062\u590d\u4f1a\u8bdd..."
                    : "\u6b63\u5728\u53d6\u6d88\uff0c\u7f51\u5361\u5c1a\u672a\u4fee\u6539...";
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
                _isCleanupDone = false;
                _adapterSelectionEnabled = true;
                _startButtonEnabled = true;
                OnPropertyChanged(nameof(AdapterSelectionEnabled));
                OnPropertyChanged(nameof(StartButtonEnabled));
                StatusText = SessionState.Network == NetworkLifecycleState.ConfigurationRestored
                    ? "\u5df2\u53d6\u6d88\uff0c\u7f51\u5361\u5df2\u6062\u590d"
                    : "\u5df2\u53d6\u6d88\uff0c\u7f51\u5361\u672a\u88ab\u4fee\u6539";
                DetailText = "\u53ef\u91cd\u65b0\u9009\u62e9\u7f51\u5361\u5f00\u59cb\u3002";
                BadgeText = "";
                ActivityText = "";
                OnPropertyChanged(nameof(ShowAdapterSelectionContent));
                OnPropertyChanged(nameof(ShowLegacyHeader));
                OnPropertyChanged(nameof(ShowLegacyRuntimeHost));
                OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
                OnPropertyChanged(nameof(ShowLegacyRuntimeCard));
            }
            catch (Exception ex)
            {
                Log("Cancel cleanup failed: " + ex.Message);
                RecordCleanupFailure(ex);
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
