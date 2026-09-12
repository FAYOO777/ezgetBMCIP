using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace EzGetBmcIp;

public sealed partial class MainViewModel : INotifyPropertyChanged, IRuntimePresentationSource
{
    int IRuntimePresentationSource.CurrentFirewallRepairStageValue => (int)CurrentFirewallRepairStage;
    string IRuntimePresentationSource.DiscoveredIp => DiscoveredIp ?? string.Empty;
    private readonly SubnetConfig _subnetConfig = new();
    private DhcpServer? _dhcpServer;
    private CancellationTokenSource? _flowCts;
    private CancellationTokenSource? _startPreflightCts;
    private Task? _flowTask;
    private Task? _endpointProbeTask;
    private Task? _pendingRecoveryTask;
    private WiredAdapter? _selectedAdapter;
    private AdapterOriginalConfig? _originalConfig;
    private NetworkRecoverySnapshot? _recoverySnapshot;
    private SubnetConfig? _recoverySubnetConfig;
    private string? _dhcpServerError;
    private DispatcherTimer? _ellipsisTimer;
    private DispatcherTimer? _dhcpElapsedTimer;
    private Stopwatch? _dhcpWaitStopwatch;
    private DateTime? _dhcpWaitStartedUtc;
    private DateTime? _lastDhcpTimerTickUtc;
    private bool _dhcpTimerLagLogged;
    private string _dhcpElapsedText = "00:00";
    private bool _hasLongDhcpWait;
    private bool _hasLongLinkWait;
    private int _ellipsisDots;
    private bool _isCleanupRunning;
    private bool _lastDhcpWaitTimedOut;
    private FirewallRiskLevel? _lastTimeoutFirewallRisk;
    private FirewallRiskLevel? _currentFirewallRisk;
    private string? _firewallNetworkCategorySnapshot;
    private string _firewallTechnicalDetail = string.Empty;
    private bool _historyRetryUsed;
    private BmcHistoryRecord? _historyRetrySuggestion;
    private bool _showSupportBundleAction;
    private BmcEndpointProbeEvidence? _lastEndpointProbeEvidence;
    private int _lastEndpointProbeAttempts;
    private BmcReachabilityResult? _lastReachabilityResult;
    private string _reachabilityDiagnostic = string.Empty;
    private string _endpointAdapterResolutionDiagnostic = string.Empty;
    private string _endpointNetworkWarning = string.Empty;
    private bool _isStartPreflightBusy;
    private bool _showStartPreflightOverlay;
    private string _startPreflightStageText = "正在读取所选网卡配置…";
    private int _startPreflightGeneration;

    private AppPhase _appPhase = AppPhase.Preparation;

    public AppPhase AppPhase
    {
        get => _appPhase;
        set
        {
            _appPhase = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAdapterVisible));
            OnPropertyChanged(nameof(IsIpCardVisible));
            OnPropertyChanged(nameof(ExitButtonText));
            OnPropertyChanged(nameof(ShowLegacyRuntimeProgress));
            OnPropertyChanged(nameof(ShowPreparationContent));
            OnPropertyChanged(nameof(ShowRuntimeHero));
            OnPropertyChanged(nameof(ShowModernRuntimeHost));
            OnPropertyChanged(nameof(ShowLegacySessionPages));
            OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
            OnPropertyChanged(nameof(ShowAdapterFirewallRetry));
            OnPropertyChanged(nameof(ModernRuntime));
        }
    }

    public bool IsAdapterVisible => _appPhase != AppPhase.Preparation;

    public CurrentSessionState SessionState { get; } = new();

    public SubnetConfig SubnetConfig => _subnetConfig;
    public string DhcpListenerStatus => _dhcpServer?.BindingDescription ?? "(not running)";

    public bool ShowHistoryRetrySuggestion => _historyRetrySuggestion is not null;

    // The formal runtime surface is a single state-driven work area. The older
    // collection of per-state cards remains only as a compatibility shell while
    // the modern host is visible and never renders beside it.
    public bool ShowModernRuntimeHost =>
        IsFirewallRepairBusy
        || (_appPhase == AppPhase.FlowRunning
            && CurrentSessionPage != SessionPageKind.Ready);

    public bool ShowLegacySessionPages => !ShowModernRuntimeHost;

    public ModernRuntimePresentation ModernRuntime => ModernRuntimePresentation.From(this);

    public bool ShowSupportBundleAction
    {
        get => _showSupportBundleAction;
        private set
        {
            _showSupportBundleAction = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowStandaloneSupportBundleAction));
            OnPropertyChanged(nameof(ShowLegacySupportBundleCard));
        }
    }

    public bool ShowStandaloneSupportBundleAction => ShowSupportBundleAction && !ShowHistoryRetrySuggestion;

    // Retained as a compatibility projection for old bindings; the formal runtime
    // host now owns all active-state progress presentation.
    public bool ShowLegacyRuntimeProgress => false;

    public bool ShowHistoryRetryCard => CurrentSessionPage == SessionPageKind.HistoryRetrySuggestion;

    public bool ShowLegacySupportBundleCard =>
        ShowStandaloneSupportBundleAction
        && !ShowModernRuntimeHost
        && CurrentSessionPage != SessionPageKind.RestoreFailed
        && CurrentSessionPage != SessionPageKind.EndpointReachable
        && CurrentSessionPage != SessionPageKind.EndpointUnreachable
        && CurrentSessionPage != SessionPageKind.DhcpTimedOut
        && CurrentSessionPage != SessionPageKind.Failure;

    public bool ShowPreparationContent =>
        _appPhase == AppPhase.Preparation && CurrentSessionPage == SessionPageKind.Ready;

    // The unified modern runtime host owns the active-flow header. Keep this
    // compatibility projection false so the retired hero never appears beside
    // the single task card during configuration.
    public bool ShowRuntimeHero => false;

    public string HistoryRetrySuggestionText => _historyRetrySuggestion is null
        ? string.Empty
        : "本次等待 DHCP 已超时；所选本机网卡曾在下列网段确认过地址可达。";
    public string HistoryAdapterName => CurrentAdapterName;
    public string HistoryLocalAddress => _historyRetrySuggestion is null
        ? string.Empty
        : _historyRetrySuggestion.LocalAddress + " / 24";
    public string HistoryBmcAddress => _historyRetrySuggestion?.BmcAddress ?? string.Empty;
    public string HistoryEndpointText => _historyRetrySuggestion is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(_historyRetrySuggestion.EndpointScheme)
            ? "地址可达"
            : _historyRetrySuggestion.EndpointScheme.ToUpperInvariant() + " · " + _historyRetrySuggestion.EndpointPort;
    public string HistoryLastConfirmedText => _historyRetrySuggestion is null
        ? string.Empty
        : _historyRetrySuggestion.LastConfirmedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public SessionPagePresentation CurrentSessionPresentation =>
        SessionPresentation.GetSessionPagePresentation(
            SessionState,
            ShowHistoryRetrySuggestion,
            PreferredBmcScheme);

    public SessionPageKind CurrentSessionPage => CurrentSessionPresentation.Page;
    public string SessionPageTitle => CurrentSessionPresentation.Title;
    public string SessionPageSummary => CurrentSessionPresentation.Summary;
    public PageActionKind RecommendedActionKind => CurrentSessionPresentation.RecommendedActionKind;

    public NetworkStatusPresentation NetworkStatusPresentation =>
        SessionPresentation.GetNetworkStatusPresentation(SessionState.Network, CurrentAdapterDisplayName);

    public string NetworkStatusText => NetworkStatusPresentation.Text;
    public string ModernNetworkStatusText => NetworkStatusText.StartsWith("本机网卡：", StringComparison.Ordinal)
        ? NetworkStatusText[5..].Trim()
        : NetworkStatusText;
    public PresentationSeverity NetworkStatusSeverity => NetworkStatusPresentation.Severity;

    public string CandidateSourceText => SessionPresentation.GetCandidateSourceText(SessionState.CandidateAddress);
    public string BrowserStatusText => SessionPresentation.GetBrowserStatusPresentation(SessionState.BrowserLaunchResult).Text;
    public PresentationSeverity BrowserStatusSeverity =>
        SessionPresentation.GetBrowserStatusPresentation(SessionState.BrowserLaunchResult).Severity;
    public string EndpointProtocolPortText => _lastReachabilityResult is null
        ? SessionPresentation.GetEndpointProtocolPortText(PreferredBmcScheme)
        : _lastReachabilityResult.HttpsPortOpen && _lastReachabilityResult.HttpPortOpen
            ? "TCP 443、80"
            : _lastReachabilityResult.HttpsPortOpen
                ? "TCP 443"
                : _lastReachabilityResult.HttpPortOpen ? "TCP 80" : "80/443 未连通";

    public FailurePresentation FailurePresentation => SessionPresentation.GetFailurePresentation(SessionState.Failure);
    public string FailureTitle => FailurePresentation.Title;
    public string FailureSummary => FailurePresentation.Summary;
    public PageActionKind FailureRecommendedActionKind => FailurePresentation.RecommendedActionKind;
    public string FailureTechnicalDetail => SessionState.Failure?.TechnicalDetail ?? string.Empty;
    public bool HasFailureTechnicalDetail => !string.IsNullOrWhiteSpace(FailureTechnicalDetail);
    public string RecoveryAdapterDisplayName => string.IsNullOrWhiteSpace(CurrentAdapterDisplayName)
        ? "所选网卡"
        : CurrentAdapterDisplayName;

    // Display-only recovery facts for the high-priority restore-failure page.
    // The underlying snapshot/configuration remains owned by the existing
    // recovery path; this projection never participates in restore decisions.
    public string RecoveryOriginalConfigDetails
    {
        get
        {
            var config = _originalConfig;
            if (config is null && _recoverySnapshot is not null)
            {
                try
                {
                    config = _recoverySnapshot.ToOriginalConfig();
                }
                catch
                {
                    return string.Empty;
                }
            }

            if (config is null)
                return string.Empty;

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

            return "原 IPv4：" + mode + "；地址：" + addresses +
                "\n原网关：" + gateways +
                "\n原 DNS：" + dns;
        }
    }

    public string FirewallSummary => SessionPresentation.GetFirewallPresentation(_currentFirewallRisk).Summary;
    public PresentationSeverity FirewallSeverity => SessionPresentation.GetFirewallPresentation(_currentFirewallRisk).Severity;
    public bool HasFirewallAssessment => _currentFirewallRisk.HasValue;
    internal string? FirewallNetworkCategorySnapshot => _firewallNetworkCategorySnapshot;
    public string FirewallTechnicalDetail => _firewallTechnicalDetail;
    public bool HasFirewallTechnicalDetail => !string.IsNullOrWhiteSpace(FirewallTechnicalDetail);

    public string CurrentAdapterName => string.IsNullOrWhiteSpace(CurrentAdapterDisplayName)
        ? "所选网卡"
        : CurrentAdapterDisplayName;

    public string LinkStatusText => _hasLongLinkWait
        ? "仍未检测到物理连接"
        : "正在等待物理连接";
    public bool ShowLongLinkWaitHint => _hasLongLinkWait && CurrentSessionPage == SessionPageKind.WaitingForLink;

    public string DhcpElapsedText
    {
        get => _dhcpElapsedText;
        private set
        {
            if (_dhcpElapsedText == value)
                return;

            _dhcpElapsedText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModernRuntime));
        }
    }

    public string DhcpMaximumWaitText => "03:00";
    public bool ShowLongDhcpWaitHint => _hasLongDhcpWait && CurrentSessionPage == SessionPageKind.WaitingForDhcp;
    public bool ShowDhcpFirewallEvidence =>
        ShowLongDhcpWaitHint && HasFirewallAssessment;
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
        && (SessionState.Failure?.Kind == FailureKind.EndpointProbeError
            || SessionState.Failure?.Kind == FailureKind.EndpointAdapterUnavailable)
        && SessionState.HasCandidateAddress;

    // ════════════════════════════════════════════════════════════════
    //  Steps
    // ════════════════════════════════════════════════════════════════

    public ObservableCollection<StepItem> Steps { get; } = new()
    {
        new StepItem("1. 连接管理口", "连接网线", "检测到 Link UP 前不会修改网卡"),
        new StepItem("2. 配置本机网卡", "配置网卡", "设置静态 IP 并启动 DHCP 服务"),
        new StepItem("3. 等待候选管理地址", "等待地址", "等待设备通过 DHCP 请求地址"),
        new StepItem("4. 确认地址可达", "确认可达", "并行检测 Ping、TCP 443 和 TCP 80；不读取网页内容"),
        new StepItem("5. 恢复原配置", "恢复网卡", "关闭 DHCP 服务并恢复本机网卡原配置")
    };

    private void RefreshStepFlags()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].IsFirst = i == 0;
            Steps[i].IsLast = i == Steps.Count - 1;
            Steps[i].PreviousState = i == 0 ? StepState.Pending : Steps[i - 1].State;
        }
    }

    private int _currentStepIndex;

    public int CurrentStepIndex
    {
        get => _currentStepIndex;
        set
        {
            _currentStepIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentStep));
            OnPropertyChanged(nameof(ModernRuntime));
        }
    }

    public StepItem? CurrentStep => Steps.Count > 0 ? Steps[CurrentStepIndex] : null;

    // ════════════════════════════════════════════════════════════════
    //  Adapter list
    // ════════════════════════════════════════════════════════════════

    public ObservableCollection<WiredAdapter> Adapters { get; } = new();

    private WiredAdapter? _selectedAdapterItem;

    public WiredAdapter? SelectedAdapterItem
    {
        get => _selectedAdapterItem;
        set
        {
            _selectedAdapterItem = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CurrentAdapterName));
            OnPropertyChanged(nameof(RecoveryAdapterDisplayName));
            OnPropertyChanged(nameof(NetworkStatusText));
            OnPropertyChanged(nameof(ModernNetworkStatusText));
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  UI bindings
    // ════════════════════════════════════════════════════════════════

    private string _statusText = "欢迎使用 IPMI/BMC 直连助手";

    public string StatusText
    {
        get => UsesSessionPresentation ? SessionPageTitle : _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _detailText = "直连服务器管理口，尝试获取候选管理地址。";

    public string DetailText
    {
        get => UsesSessionPresentation ? SessionPageSummary : _detailText;
        set { _detailText = value; OnPropertyChanged(); }
    }

    private string? _initError;

    private string _activityText = "";

    public string ActivityText
    {
        get => _activityText;
        set { _activityText = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModernRuntime)); }
    }

    private string _badgeText = "等待中";

    public string BadgeText
    {
        get => _badgeText;
        set { _badgeText = value; OnPropertyChanged(); }
    }

    private StepState _badgeState = StepState.Pending;

    public StepState BadgeState
    {
        get => _badgeState;
        set { _badgeState = value; OnPropertyChanged(); }
    }

    private bool _adapterSelectionEnabled = true;

    public bool AdapterSelectionEnabled
    {
        get => _adapterSelectionEnabled && !IsFirewallRepairBusy && !IsStartPreflightBusy;
        set { _adapterSelectionEnabled = value; OnPropertyChanged(); }
    }

    private bool _startButtonEnabled = true;

    public bool StartButtonEnabled
    {
        get => _startButtonEnabled && !IsFirewallRepairBusy && !IsStartPreflightBusy;
        set { _startButtonEnabled = value; OnPropertyChanged(); }
    }

    public bool IsStartPreflightBusy => _isStartPreflightBusy;
    public bool ShowStartPreflightOverlay => _showStartPreflightOverlay;
    public string StartPreflightTitle => "正在检查网络环境";
    public string StartPreflightStageText => _startPreflightStageText;
    public string StartPreflightDetailText => "当前尚未修改网卡。";

    private bool _exitButtonEnabled = true;

    public bool ExitButtonEnabled
    {
        get => _exitButtonEnabled;
        set
        {
            _exitButtonEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExitActionEnabled));
        }
    }

    public bool IsExitActionEnabled =>
        !IsFirewallRepairBusy && ExitButtonEnabled && SessionPresentation.IsExitActionEnabled(SessionState.ExitIntent);

    private bool _isCleanupDone;

    public bool IsCleanupDone
    {
        get => _isCleanupDone;
        set { _isCleanupDone = value; OnPropertyChanged(); }
    }

    public string? DiscoveredIp
    {
        get => SessionState.CandidateAddress?.IPv4Address;
        set
        {
            // Compatibility setter for the current UI/test surface. Production flow writes
            // candidate facts through SetCandidateAddress with its actual source instead.
            if (string.IsNullOrWhiteSpace(value))
                ClearCandidateAddress();
            else
                SetCandidateAddress(value, CandidateAddressSource.DhcpAck);
        }
    }

    // Compatibility projection for current XAML. It means only that a candidate exists,
    // not that TCP 80/443 is reachable or that discovery succeeded.
    public bool IsIpDiscovered => SessionState.HasCandidateAddress;

    public bool IsIpCardVisible => IsIpDiscovered && _appPhase == AppPhase.FlowRunning;

    public string DiscoveredIpUrl => string.IsNullOrWhiteSpace(DiscoveredIp)
        ? ""
        : PreferredBmcScheme + "://" + DiscoveredIp;

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
            OnPropertyChanged(nameof(SessionPageSummary));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(ModernRuntime));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _endpointStatusText = "等待候选管理地址的可达性检测。";

    public string EndpointStatusText
    {
        get => _endpointStatusText;
        set { _endpointStatusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModernRuntime)); }
    }

    private bool _isEndpointProbeRunning;

    public bool IsEndpointProbeRunning
    {
        get => _isEndpointProbeRunning;
        private set
        {
            _isEndpointProbeRunning = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private bool _isAdvancedSubnetExpanded;

    public bool IsAdvancedSubnetExpanded
    {
        get => _isAdvancedSubnetExpanded;
        set
        {
            _isAdvancedSubnetExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AdvancedSubnetToggleText));
        }
    }

    public string AdvancedSubnetToggleText => IsAdvancedSubnetExpanded ? "收起" : "修改";

    private string _copyButtonText = "复制地址";

    public string CopyButtonText
    {
        get => _copyButtonText;
        set { _copyButtonText = value; OnPropertyChanged(); }
    }

    private DispatcherTimer? _copyFeedbackTimer;

    public string VersionText => AppVersionText.Get();
    public string ExitButtonText => SessionPresentation.GetExitActionText(SessionState.ExitIntent);

    private bool _isFlowStarted;

    public bool IsFlowStarted
    {
        get => _isFlowStarted;
        set { _isFlowStarted = value; OnPropertyChanged(); }
    }

    private string _adapterCardLine1 = "";

    public string AdapterCardLine1
    {
        get => _adapterCardLine1;
        set { _adapterCardLine1 = value; OnPropertyChanged(); }
    }

    private string _adapterCardLine2 = "";

    public string AdapterCardLine2
    {
        get => _adapterCardLine2;
        set { _adapterCardLine2 = value; OnPropertyChanged(); }
    }

    // ════════════════════════════════════════════════════════════════
    //  Commands
    // ════════════════════════════════════════════════════════════════

    public ICommand StartCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand CopyIpCommand { get; }
    public ICommand GoNextCommand { get; }
    public ICommand ToggleAdvancedSubnetCommand { get; }
    public ICommand RetryEndpointCommand { get; }
    public ICommand OpenHttpsCommand { get; }
    public ICommand OpenHttpCommand { get; }
    public ICommand OpenManagementPageCommand { get; }
    public ICommand PrepareHistoryRetryCommand { get; }

    // ════════════════════════════════════════════════════════════════
    //  Events (for window interaction)
    // ════════════════════════════════════════════════════════════════

    public event Action? RequestClose;
    public event Action<string>? OpenBrowserRequested;
    internal event Func<ConsentNotice, bool>? ConsentRequested;

    // Test-only seams for the read-only start preflight. They are intentionally
    // internal so production behavior continues to use the real network and
    // firewall readers while UI tests can exercise slow, failed, and cancelled
    // checks without changing an adapter.
    internal Func<WiredAdapter, AdapterOriginalConfig>? CaptureOriginalConfigForTests { get; set; }
    internal Func<WiredAdapter, Task<FirewallAssessment>>? AssessFirewallForTestsAsync { get; set; }
    internal Func<IPAddress, IPAddress, CancellationToken, Task<BmcReachabilityResult>>? ReachabilityProbeForTests { get; set; }

    public MainViewModel()
    {
        RefreshStepFlags();
        StartCommand = new RelayCommand(_ =>
        {
            _flowTask = StartFlowAsync();
            return _flowTask;
        }, _ => StartButtonEnabled);
        ExitCommand = new RelayCommand(_ => RequestClose?.Invoke());
        CopyIpCommand = new RelayCommand(_ => CopyIp());
        GoNextCommand = new RelayCommand(_ => GoNext());
        ToggleAdvancedSubnetCommand = new RelayCommand(_ => IsAdvancedSubnetExpanded = !IsAdvancedSubnetExpanded);
        RetryEndpointCommand = new RelayCommand(_ =>
        {
            _endpointProbeTask = RetryEndpointProbeAsync();
            return _endpointProbeTask;
        }, _ => SessionState.HasCandidateAddress && !IsEndpointProbeRunning);
        OpenHttpsCommand = new RelayCommand(_ => OpenBrowserForScheme("https"), _ => SessionState.HasCandidateAddress);
        OpenHttpCommand = new RelayCommand(_ => OpenBrowserForScheme("http"), _ => SessionState.HasCandidateAddress);
        OpenManagementPageCommand = new RelayCommand(
            _ => OpenBrowserForScheme(PreferredBmcScheme),
            _ => SessionState.IsDiscoverySuccessful && IsPreferredManagementScheme);
        PrepareHistoryRetryCommand = new RelayCommand(
            _ => PrepareHistoryRetryAsync(),
            _ => _historyRetrySuggestion is not null && !_isCleanupRunning);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            LogInfo("Adapter enumeration started");
            var adapters = await Task.Run(NetworkConfigManager.GetWiredAdapters);
            LogInfo("Adapter enumeration done: " + adapters.Count + " adapter(s) found");

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
                throw new InvalidOperationException("未检测到可用网卡，请确认网卡驱动已安装。");
            }

            foreach (var a in adapters)
            {
                Adapters.Add(a);
                LogInfo("Adapter: " + a.Name + " | " + a.Description + " | id=" + a.Id + " | mac=" + a.MacAddress);
            }
            SelectedAdapterItem = Adapters[0];
            LogInfo("Selected adapter: " + Adapters[0].Name);
        }
        catch (Exception ex)
        {
            LogInfo("Adapter enumeration failed: " + ex.Message);
            if (!SessionState.HasFailure)
                FailSession(FailureKind.InitializationFailed, ex.Message);
            _initError = ex.Message;
        }
    }

    private async Task RecoverPendingNetworkConfigurationAsync(IReadOnlyList<WiredAdapter> adapters)
    {
        if (!NetworkRecoveryStore.TryLoad(out var snapshot, out var loadError))
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
        LogInfo("Pending recovery found: session=" + snapshot.SessionId + " adapter=" + snapshot.AdapterName);

        try
        {
            _recoverySnapshot = snapshot;
            _selectedAdapter = snapshot.ToAdapter();
            _originalConfig = snapshot.ToOriginalConfig(_selectedAdapter);
            _recoverySubnetConfig = snapshot.ToSubnetConfig();
            SetNetworkState(NetworkLifecycleState.Restoring);
            using var recoveryCts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            await NetworkRecoveryStore.ExecuteWithRecoveryLockAsync(async () =>
            {
                if (!NetworkRecoveryStore.TryLoad(out var currentSnapshot, out var currentError))
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
            StatusText = "✅ 上次网卡配置已恢复";
            DetailText = "异常退出留下的网络配置已经处理，可以继续使用。";
            LogInfo("Pending recovery completed: session=" + snapshot.SessionId);
            // This process did not mutate its own session adapter. Once the prior session's
            // snapshot is handled, the new session starts from an untouched lifecycle.
            SetNetworkState(NetworkLifecycleState.Untouched);
            _recoverySnapshot = null;
            _recoverySubnetConfig = null;
            _selectedAdapter = null;
            _originalConfig = null;
        }
        catch (Exception ex)
        {
            SetNetworkState(NetworkLifecycleState.RestoreFailed);
            StatusText = "❌ 上次网卡配置恢复失败";
            DetailText = "请重新连接网卡「" + snapshot.AdapterName + "」后重启程序。恢复记录会继续保留。";
            throw new InvalidOperationException(
                "检测到上次未完成的网卡配置，但自动恢复失败。请重新连接原网卡「" +
                snapshot.AdapterName + "」后重试。原始错误：" + ex.Message,
                ex);
        }
    }

    private void GoNext()
    {
        if (_initError is not null)
        {
            StatusText = "❌ 操作失败";
            DetailText = _initError;
            BadgeState = StepState.Failed;
            BadgeText = "! 失败";
            return;
        }

        if (!RequestConsent(ConsentNotice.CreateModernUsageRisk()))
        {
            LogInfo("Usage risk consent declined");
            return;
        }

        AppPhase = AppPhase.AdapterSelection;
        StatusText = "选择连接 BMC 的网卡";
        DetailText = "选择直连服务器管理口的有线网卡，然后点击「开始」。";
    }

    private async Task StartFlowAsync()
    {
        if (IsFirewallRepairBusy || IsStartPreflightBusy) return;
        SetFirewallRepairFeedback("");
        if (SelectedAdapterItem is null)
            return;

        ClearCurrentFirewallAssessment();
        _firewallNetworkCategorySnapshot = null;
        StopDhcpElapsedTimer(reset: true);

        if (!_subnetConfig.IsPrivateSubnet)
        {
            var message = _subnetConfig.ValidationError ?? "自定义网段无效。";
            LogInfo("Flow blocked: invalid subnet " + _subnetConfig.ServerDisplay + " - " + message);
            StatusText = "❌ 网段不可用";
            DetailText = message + " 公网地址可能被系统代理或路由策略拦截，直连场景请使用私有网段。";
            BadgeState = StepState.Failed;
            BadgeText = "! 网段错误";
            return;
        }

        var preflightAdapter = SelectedAdapterItem;
        using var preflightCts = new CancellationTokenSource();
        _startPreflightCts = preflightCts;
        var preflightToken = preflightCts.Token;
        var preflightGeneration = ++_startPreflightGeneration;
        SetStartPreflightStage("正在读取所选网卡配置…");
        SetStartPreflightBusy(true);
        _ = ShowStartPreflightOverlayAfterDelayAsync(preflightGeneration, preflightToken);

        AdapterOriginalConfig originalConfig;
        FirewallAssessment firewallAssessment;
        var originalConfigCaptured = false;
        try
        {
            // Give WPF one render opportunity before the potentially slow read.
            await Task.Yield();
            preflightToken.ThrowIfCancellationRequested();
            originalConfig = await Task.Run(
                () => CaptureOriginalConfigForTests?.Invoke(preflightAdapter)
                    ?? NetworkConfigManager.CaptureOriginalConfig(preflightAdapter),
                preflightToken);
            originalConfigCaptured = true;

            preflightToken.ThrowIfCancellationRequested();
            SetStartPreflightStage("正在检查防火墙规则…");
            firewallAssessment = AssessFirewallForTestsAsync is not null
                ? await AssessFirewallForTestsAsync(preflightAdapter)
                : await AssessFirewallAsync(preflightAdapter, preflightToken);
            preflightToken.ThrowIfCancellationRequested();
            CaptureFirewallNetworkCategorySnapshot(firewallAssessment);
            SetStartPreflightStage("检查完成，正在显示风险告知…");
            SetCurrentFirewallAssessment(firewallAssessment);
            _showStartPreflightOverlay = false;
            OnPropertyChanged(nameof(ShowStartPreflightOverlay));

            if (!RequestConsent(ConsentNotice.CreateModernNetworkChange(
                preflightAdapter, _subnetConfig, originalConfig, firewallAssessment)))
            {
                LogInfo("Network change consent declined");
                return;
            }
        }
        catch (OperationCanceledException) when (preflightToken.IsCancellationRequested)
        {
            LogInfo("Start preflight cancelled");
            return;
        }
        catch (Exception ex)
        {
            LogInfo("Original configuration or firewall preflight failed: " + ex.Message);
            FailSession(
                originalConfigCaptured ? FailureKind.Unexpected : FailureKind.OriginalConfigurationCaptureFailed,
                ex.Message);
            StatusText = originalConfigCaptured ? "❌ 无法检查网络环境" : "❌ 无法读取网卡原始配置";
            DetailText = "为避免覆盖现有网络设置，已停止操作：" + ex.Message;
            BadgeState = StepState.Failed;
            BadgeText = "! 无法确认";
            return;
        }
        finally
        {
            preflightCts.Cancel();
            if (ReferenceEquals(_startPreflightCts, preflightCts))
                _startPreflightCts = null;
            SetStartPreflightBusy(false);
        }

        _selectedAdapter = preflightAdapter;
        _originalConfig = originalConfig;
        _recoverySnapshot = null;
        _recoverySubnetConfig = null;
        ResetSessionStateForNewFlow();
        AdapterSelectionEnabled = false;
        StartButtonEnabled = false;
        AppPhase = AppPhase.FlowRunning;
        ResetStepsForNewFlow();
        PreferredBmcScheme = "https";
        EndpointStatusText = "等待候选管理地址的可达性检测。";
        SetEndpointProbeOutcome(null);
        CopyButtonText = "复制地址";
        AdapterCardLine1 = "✓ " + _selectedAdapter.DisplayName;
        AdapterCardLine2 = "";
        _flowCts = new CancellationTokenSource();
        var flowToken = _flowCts.Token;
        _dhcpServerError = null;
        _lastDhcpWaitTimedOut = false;
        _lastTimeoutFirewallRisk = null;
        SetHistoryRetrySuggestion(null);
        ShowSupportBundleAction = false;

        LogInfo("Flow started: adapter=" + _selectedAdapter.Name + " subnet=" + _subnetConfig.ServerDisplay + " pool=" + _subnetConfig.PoolStart);

        try
        {
            await RunLinkThenConfigureAsync(
                WaitForLinkAsync,
                ConfigureLocalAdapterAsync,
                flowToken);
            flowToken.ThrowIfCancellationRequested();
            var discovery = await DiscoverBmcAddressAsync(flowToken);
            flowToken.ThrowIfCancellationRequested();
            LogInfo("Flow: BMC candidate address discovered " + discovery.IpAddress);
            if (discovery.Reachability is not null)
                await PresentReachabilityAsync(discovery.IpAddress, discovery.Reachability, autoOpen: true, flowToken);
            else
                await ProbeBmcEndpointAsync(discovery.IpAddress, autoOpen: true, flowToken);
        }
        catch (OperationCanceledException)
        {
            LogInfo("Flow cancelled");
            MarkFlowCancelled();
            StatusText = "正在退出...";
            DetailText = SessionState.RequiresNetworkRecovery
                ? "正在恢复使用工具前的网卡配置。"
                : SessionState.RequiresCleanup
                    ? "正在清理当前恢复会话。"
                    : "正在安全退出。";
            StopEllipsis();
        }
        catch (Exception ex) when (flowToken.IsCancellationRequested)
        {
            LogInfo("Flow cancellation won over a concurrent error: " + ex.Message);
            MarkFlowCancelled();
            StatusText = "正在退出...";
            DetailText = SessionState.RequiresNetworkRecovery
                ? "正在恢复使用工具前的网卡配置。"
                : SessionState.RequiresCleanup
                    ? "正在清理当前恢复会话。"
                    : "正在安全退出。";
            StopEllipsis();
        }
        catch (Exception ex)
        {
            LogInfo("Flow failed: " + ex.Message);
            if (!SessionState.HasFailure)
                FailSession(FailureKind.Unexpected, ex.Message);
            else
                SetWorkflowState(DiscoveryWorkflowState.Failed);
            var failureDetail = BuildFailureDetail(CurrentStepIndex, ex.Message);
            if (SessionState.RequiresNetworkRecovery)
            {
                failureDetail += " 网卡已经开始修改，请先使用底部退出操作执行恢复；恢复成功前不能重新开始。";
            }
            else if (SessionState.RequiresCleanup)
            {
                failureDetail += " 恢复会话尚待清理，请先使用底部退出操作安全退出。";
            }
            MarkCurrentFailure(failureDetail);
            StatusText = "❌ 操作失败";
            DetailText = failureDetail;
            AdapterSelectionEnabled = !SessionState.RequiresCleanup;
            StartButtonEnabled = !SessionState.RequiresCleanup;
            BadgeState = StepState.Failed;
            BadgeText = "! 失败";
            ShowSupportBundleAction = true;
            if (_lastDhcpWaitTimedOut)
                OfferHistoryRetryIfEligible();
            StopEllipsis();
        }
    }

    private async Task ConfigureLocalAdapterAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_selectedAdapter is null || _originalConfig is null)
            throw new InvalidOperationException("未确认网卡原始配置，已停止修改网络设置。");

        SetWorkflowState(DiscoveryWorkflowState.ConfiguringNetwork);
        try
        {
            var captured = await NetworkConfigManager.ResolveAndCaptureOriginalConfigAsync(_selectedAdapter, ct);
            _selectedAdapter = captured.Adapter;
            _originalConfig = captured.OriginalConfig;
        }
        catch (Exception ex)
        {
            SetFailure(FailureKind.OriginalConfigurationCaptureFailed, ex.Message);
            throw;
        }

        SetStep(1, StepState.Active, "正在配置本机网卡：" + _selectedAdapter.Name);
        SetBusy("正在配置本机网卡...", "已确认原始配置，正在将网卡切换到 " + _subnetConfig.ServerDisplay + "。");
        StartEllipsis();

        LogInfo("Original config: dhcp=" + _originalConfig.DhcpEnabled +
            " dnsFromDhcp=" + _originalConfig.DnsServersFromDhcp +
            " addrs=" + _originalConfig.StaticAddresses.Count +
            " gw=" + _originalConfig.Gateways.Count +
            " gwMetrics=" + _originalConfig.GatewayMetrics.Count +
            " dns=" + _originalConfig.DnsServers.Count);

        _recoverySnapshot = NetworkRecoveryStore.Save(_selectedAdapter, _originalConfig, _subnetConfig);
        LogInfo("Recovery snapshot saved: session=" + _recoverySnapshot.SessionId);
        try
        {
            NetworkRecoveryStore.StartWatchdog(_recoverySnapshot, LogInfo);
        }
        catch
        {
            NetworkRecoveryStore.DeleteIfSessionMatches(_recoverySnapshot.SessionId);
            _recoverySnapshot = null;
            throw;
        }

        SetNetworkState(NetworkLifecycleState.RecoveryPrepared);
        SetNetworkState(NetworkLifecycleState.TemporaryConfigurationMayBeActive);
        LogInfo("Adapter mutation boundary entered after Link UP");
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
        LogInfo("Static IP set: " + _subnetConfig.ServerDisplay);
        try
        {
            _dhcpServer = new DhcpServer(_subnetConfig, _selectedAdapter);
            _dhcpServer.Logger = msg => LogInfo("[DHCP] " + msg);
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
        await CompleteStepAsync(1, "✅ 本机网卡配置完成：" + _subnetConfig.ServerDisplay, ct);
        StopEllipsis();
    }

    private void SetEndpointProbeOutcome(BmcEndpointProbeOutcome? outcome)
    {
        if (outcome is null)
            _endpointAdapterResolutionDiagnostic = string.Empty;
        _lastReachabilityResult = null;
        _reachabilityDiagnostic = string.Empty;
        _lastEndpointProbeEvidence = outcome?.Evidence;
        _lastEndpointProbeAttempts = outcome?.Attempts ?? 0;
        _endpointNetworkWarning = outcome?.VerifiedEndpoint is null
            ? string.Empty
            : EndpointNetworkEnvironment.GetPotentialInterferenceWarning();
        OnPropertyChanged(nameof(EndpointVerificationText));
        OnPropertyChanged(nameof(EndpointVerificationDiagnosticText));
        OnPropertyChanged(nameof(EndpointProtocolPortText));
        OnPropertyChanged(nameof(EndpointNetworkWarning));
        OnPropertyChanged(nameof(HasEndpointNetworkWarning));
        OnPropertyChanged(nameof(ModernRuntime));
    }

    private void SetReachabilityOutcome(BmcReachabilityResult? result)
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
        OnPropertyChanged(nameof(ModernRuntime));
    }

    private async Task<BmcEndpointProbeRequest> CreateEndpointProbeRequestAsync(
        IPAddress targetAddress,
        CancellationToken cancellationToken)
    {
        var adapter = _selectedAdapter ?? SelectedAdapterItem;
        if (adapter is null)
            throw new InvalidOperationException("未确认用于直连 BMC 的网卡。");
        if (!IPAddress.TryParse(_subnetConfig.ServerIp, out var sourceAddress) ||
            sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("当前临时本机 IPv4 地址无效。");

        var resolution = await AdapterIdentityResolver.ResolveAsync(
            adapter,
            sourceAddress,
            cancellationToken,
            message => LogInfo("[AdapterResolve] " + message));
        _endpointAdapterResolutionDiagnostic = resolution.Diagnostic + "; attempts=" + resolution.Attempts;
        OnPropertyChanged(nameof(EndpointVerificationDiagnosticText));
        if (!resolution.Success)
            throw new EndpointAdapterUnavailableException(resolution);

        var networkInterface = resolution.NetworkInterface;
        var interfaceIndex = resolution.Candidate.InterfaceIndex;
        if (interfaceIndex <= 0)
            throw new InvalidOperationException("无法读取所选网卡的 IPv4 接口索引。");

        var leaseMac = _dhcpServer?.LastAssignedLease?.MacAddress;
        return new BmcEndpointProbeRequest
        {
            TargetAddress = targetAddress,
            SourceAddress = sourceAddress,
            AdapterName = networkInterface.Name,
            AdapterId = networkInterface.Id,
            InterfaceIndex = interfaceIndex,
            ExpectedPeerMac = leaseMac is { Length: > 0 }
                ? string.Join("-", leaseMac.Select(value => value.ToString("X2")))
                : string.Empty
        };
    }

    private async Task<BmcReachabilityResult> ProbeReachabilityAsync(
        IPAddress targetAddress,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(_subnetConfig.ServerIp, out var sourceAddress) ||
            sourceAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("当前临时本机 IPv4 地址无效。");

        var probe = ReachabilityProbeForTests;
        if (probe is not null)
            return await probe(targetAddress, sourceAddress, cancellationToken);

        return await BmcReachabilityProbe.ProbeAsync(
            targetAddress,
            sourceAddress,
            cancellationToken,
            message => LogInfo("[Reachability] " + message),
            BmcReachabilityProbe.DefaultTimeout);
    }

    public string EndpointVerificationText
    {
        get
        {
            var reachability = _lastReachabilityResult;
            if (reachability is not null)
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
            if (evidence is null)
                return "尚未完成地址可达性检测。";

            if (evidence.HttpResponseReceived && BmcEndpointProbe.IsAcceptedManagementHttpStatus(evidence.HttpStatusCode))
                return "兼容性严格探测曾从所选直连网卡收到 " + (evidence.TlsEstablished ? "HTTPS" : "HTTP") +
                    " 响应：HTTP " + evidence.HttpStatusCode + "；邻居 MAC：" +
                    (string.IsNullOrWhiteSpace(evidence.NeighborMac) ? "未记录" : evidence.NeighborMac) +
                    "。当前成功判定以 Ping/TCP 可达性为准。";

            if (evidence.HttpResponseReceived)
                return "兼容性严格探测收到 HTTP " + evidence.HttpStatusCode +
                    "；该网页响应不参与当前地址可达性判定。";

            return "兼容性严格探测未收到 HTTP/HTTPS 响应；当前地址可达性由 Ping/TCP 80/443 决定，最后阶段：" +
                (string.IsNullOrWhiteSpace(evidence.FailureStage.ToString()) ? "未知" : evidence.FailureStage.ToString()) + "。";
        }
    }

    public string EndpointVerificationDiagnosticText
    {
        get
        {
            if (_lastReachabilityResult is not null)
                return _reachabilityDiagnostic;

            var evidence = _lastEndpointProbeEvidence;
            if (evidence is null)
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
                "; peerMacMatchesExpected=" + (evidence.PeerMacMatchesExpected?.ToString() ?? "unknown") +
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

    private void OnDhcpServerError(object? sender, string message)
    {
        _dhcpServerError = message;
        LogInfo("[DHCP] Error: " + message);
    }

    private async Task WaitForLinkAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LogInfo("Link wait started");
        SetLongLinkWait(false);
        SetWorkflowState(DiscoveryWorkflowState.WaitingForLink);
        SetStep(0, StepState.Active, "请用网线连接服务器的 IPMI 管理口，正在等待 Link UP。");
        SetBusy("请插入网线", "等待检测到网线连接，预计几秒内完成。");
        StartEllipsis();

        var warned = false;
        var startTime = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await NetworkConfigManager.IsLinkUpAsync(_selectedAdapter!, ct))
                {
                    LogInfo("Link detected");
                    await CompleteStepAsync(0, "✅ 网线已连接，链路已 UP；现在开始配置本机网卡", ct);
                    StopEllipsis();
                    return;
                }

                if (!warned && (DateTime.UtcNow - startTime).TotalSeconds > 60)
                {
                    LogInfo("Link wait: 60s warning");
                    SetLongLinkWait(true);
                    warned = true;
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
        finally
        {
            SetLongLinkWait(false);
        }
    }

    private async Task<DhcpLease> WaitForDhcpLeaseAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        LogInfo("DHCP lease wait started");
        SetWorkflowState(DiscoveryWorkflowState.WaitingForDhcp);
        SetStep(2, StepState.Active, "链路已 UP，正在等待 IPMI 通过 DHCP 获取地址，最多等待 3 分钟。");
        SetBusy("正在等待 IPMI 获取 IP...", "本机网卡已切换到临时直连配置，正在等待设备发起 DHCP。");
        StartEllipsis();
        StartDhcpElapsedTimer();

        var tcs = new TaskCompletionSource<DhcpLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, DhcpLease lease) => tcs.TrySetResult(lease);
        void ErrorHandler(object? sender, string message) =>
            tcs.TrySetException(new InvalidOperationException(message));

        if (_dhcpServer is not null)
        {
            _dhcpServer.LeaseAssigned += Handler;
            _dhcpServer.ErrorEncountered += ErrorHandler;
            var existingLease = _dhcpServer.LastAssignedLease;
            if (existingLease is not null)
            {
                LogInfo("DHCP lease was assigned before lease wait started; using cached lease: " +
                    existingLease.IpAddress);
                tcs.TrySetResult(existingLease);
            }
        }
        var dhcpTimedOut = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(_dhcpServerError))
                throw new InvalidOperationException(_dhcpServerError);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

            var lease = await tcs.Task;
            ct.ThrowIfCancellationRequested();
            LogInfo("DHCP lease acquired: IP=" + lease.IpAddress + " MAC=" + (lease.MacAddress.Length > 0 ? string.Join("-", lease.MacAddress.Select(b => b.ToString("X2"))) : "none"));
            RecordDhcpAck(lease);
            SetStep(2, StepState.Done, "✅ 已向直连设备分配候选地址：" + lease.IpAddress);
            StopEllipsis();
            return lease;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            dhcpTimedOut = true;
            SetDhcpElapsedToMaximum();
            var adapter = _selectedAdapter ?? SelectedAdapterItem;
            var firewallAssessment = adapter is null
                ? FirewallAssessmentService.CreateUnknown(
                    FirewallAssessmentService.GetCurrentExecutablePath(),
                    "No selected adapter was available for the timeout assessment.")
                : await AssessFirewallAsync(adapter, ct);
            _lastDhcpWaitTimedOut = true;
            _lastTimeoutFirewallRisk = firewallAssessment.RiskLevel;
            SetCurrentFirewallAssessment(firewallAssessment);
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
            StopDhcpElapsedTimer(reset: !dhcpTimedOut);
            if (_dhcpServer is not null)
            {
                _dhcpServer.LeaseAssigned -= Handler;
                _dhcpServer.ErrorEncountered -= ErrorHandler;
            }
        }
    }

    private async Task<BmcDiscoveryResult> DiscoverBmcAddressAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IPAddress.TryParse(_subnetConfig.PoolStart, out var configuredAddress))
        {
            var lease = await WaitForDhcpLeaseAsync(ct);
            return new BmcDiscoveryResult { IpAddress = lease.IpAddress, Source = BmcDiscoverySource.Dhcp };
        }

        var discovery = await BmcDiscovery.WaitForAddressAsync(
            configuredAddress,
            async token => (await WaitForDhcpLeaseAsync(token)).IpAddress,
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
                    // The retained-address convenience race must never turn a
                    // failed lightweight check into a DHCP failure.
                    LogInfo("Configured-address reachability unavailable: " + ex.Message);
                    return null!;
                }
            },
            ct);
        ct.ThrowIfCancellationRequested();

        if (discovery.Source == BmcDiscoverySource.ExistingConfiguredAddress)
        {
            SetCandidateAddress(
                discovery.IpAddress.ToString(),
                CandidateAddressSource.ExistingConfiguredAddress);
            LogInfo("BMC address reachable at configured address " + discovery.IpAddress +
                "; retained address path carried lightweight reachability evidence and ended DHCP waiting.");
        }
        else if (!SessionState.HasCandidateAddress ||
                 !string.Equals(SessionState.CandidateAddress?.IPv4Address, discovery.IpAddress.ToString(), StringComparison.Ordinal))
        {
            // The DHCP wait normally recorded this before BmcDiscovery returned. Keep the
            // discovery result authoritative if a future caller reaches this path directly.
            SetCandidateAddress(discovery.IpAddress.ToString(), CandidateAddressSource.DhcpAck);
        }

        return discovery;
    }

    private async Task RetryEndpointProbeAsync()
    {
        if (IsFirewallRepairBusy) return;
        if (!IPAddress.TryParse(SessionState.CandidateAddress?.IPv4Address, out var ipAddress))
            return;

        try
        {
            var token = _flowCts?.Token ?? CancellationToken.None;
            await ProbeBmcEndpointAsync(ipAddress, autoOpen: true, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogInfo("BMC endpoint retry failed: " + ex.Message);
            EndpointStatusText = "重新检测地址可达性时遇到问题：" + ex.Message;
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

    private async Task<bool> ProbeBmcEndpointAsync(
        IPAddress ipAddress,
        bool autoOpen,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsEndpointProbeRunning = true;
        BeginEndpointProbe();
        EndpointStatusText = "已取得候选管理地址，正在进行 Ping 和 TCP 80/443 可达性检测...";
        SetStep(3, StepState.Active, "已取得候选管理地址，正在并行检测 Ping、TCP 443 和 TCP 80。");
        SetBusy("正在确认候选地址可达性...", "候选管理地址：" + ipAddress + "；最长检测 5 秒，不读取网页内容。 ");
        StartEllipsis();

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
            LogInfo("BMC reachability probe failed: " + ex.Message);
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
        if (reachability is null)
            return Task.FromResult(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (!reachability.IsReachable)
        {
            SetWorkflowState(DiscoveryWorkflowState.EndpointUnreachable);
            PreferredBmcScheme = "https";
            EndpointStatusText = "候选管理地址已保留，但暂未确认可达。";
            SetStep(3, StepState.Pending, "⚠ 已分配候选地址，但暂未确认 Ping 或 TCP 80/443 可达。");
            StatusText = "已获取候选地址，尚未确认可达";
            DetailText = "5 秒内未收到 Ping 或 TCP 80/443 的成功响应。地址仍会保留；可以重新检测，或手动尝试 HTTPS / HTTP。网页内容和状态码不会影响地址分配结果。";
            BadgeState = StepState.Pending;
            BadgeText = "等待确认";
            ActivityText = "候选地址已保留，等待用户重新检测或手动访问。";
            StopEllipsis();
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
        LogInfo("BMC address reachable: ip=" + ipAddress +
            " ping=" + reachability.PingSucceeded +
            " tcp443=" + reachability.HttpsPortOpen +
            " tcp80=" + reachability.HttpPortOpen);
        RememberReachableBmc(ipAddress, reachability);

        if (autoOpen && !string.IsNullOrWhiteSpace(reachability.PreferredUrl))
            OpenBrowser(reachability.PreferredUrl);

        SetStep(3, StepState.Done, "✅ 地址已分配并确认可达");
        SetStep(4, StepState.Pending, "完成浏览器中的操作后，使用右下角操作恢复本机网卡原配置。");
        StatusText = "地址已分配并确认可达";
        var icmpDetail = reachability.PingSucceeded
            ? string.Empty
            : "设备未响应 ICMP Ping，但 TCP 握手成功，仍判定地址可达。 ";
        var browserDetail = string.IsNullOrWhiteSpace(reachability.PreferredUrl)
            ? icmpDetail + "80/443 暂无可连接端口；网页服务可能未启动或使用其他端口。可以稍后重新检测或手动尝试 HTTPS / HTTP。"
            : SessionState.BrowserLaunchResult == BrowserLaunchResult.RequestFailed
                ? icmpDetail + "已确认地址可达，但浏览器启动请求失败；可以复制地址或手动打开。"
                : icmpDetail + "已确认地址可达，已请求浏览器打开 " + reachability.PreferredUrl + "。网页是否最终加载以浏览器实际显示为准。";
        DetailText = browserDetail + " 完成后点击右下角恢复网卡并退出。";
        BadgeState = StepState.Done;
        BadgeText = "✓ 已完成";
        ActivityText = "地址已分配并确认可达；完成浏览器中的操作后恢复网卡。";
        StopEllipsis();
        return Task.FromResult(true);
    }

    private void OpenBrowserForScheme(string scheme)
    {
        var candidateAddress = SessionState.CandidateAddress?.IPv4Address;
        if (!string.IsNullOrWhiteSpace(candidateAddress))
            OpenBrowser(scheme + "://" + candidateAddress);
    }

    private void OpenBrowser(string url)
    {
        var handler = OpenBrowserRequested;
        if (handler is null)
        {
            SetBrowserLaunchResult(BrowserLaunchResult.RequestFailed);
            LogInfo("Browser open failed: no browser-launch handler is registered.");
            return;
        }

        try
        {
            handler.Invoke(url);
            SetBrowserLaunchResult(BrowserLaunchResult.Requested);
            LogInfo("Browser open requested for " + url);
        }
        catch (Exception ex)
        {
            SetBrowserLaunchResult(BrowserLaunchResult.RequestFailed);
            LogInfo("Browser open failed: " + ex.Message);
        }
    }

    private async Task CompleteStepAsync(int index, string description, CancellationToken ct)
    {
        SetStep(index, StepState.Done, description);
        await Task.Delay(1500, ct);
    }

    private void OfferHistoryRetryIfEligible()
    {
        if (_selectedAdapter is null)
            return;

        var adapter = _selectedAdapter;
        var history = BmcHistoryStore.LoadForAdapter(adapter);
        if (history is null)
            return;

        if (!BmcHistoryRetryPolicy.ShouldOffer(
                adapter, _subnetConfig, history, _historyRetryUsed, _lastTimeoutFirewallRisk))
            return;

        SetHistoryRetrySuggestion(history);
        LogInfo("History retry offered: adapter=" + adapter.Name +
            " previousBmc=" + history.BmcAddress + " previousLocal=" + history.LocalAddress);
    }

    private void SetHistoryRetrySuggestion(BmcHistoryRecord? history)
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
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task PrepareHistoryRetryAsync()
    {
        if (IsFirewallRepairBusy) return;
        var history = _historyRetrySuggestion;
        var adapter = _selectedAdapter ?? SelectedAdapterItem;
        if (history is null || adapter is null || _isCleanupRunning)
            return;

        if (!history.TryApplyTo(new SubnetConfig()))
        {
            SetHistoryRetrySuggestion(null);
            return;
        }

        if (!RequestConsent(ConsentNotice.CreateHistoryRetryPreparation(adapter, history)))
        {
            LogInfo("History retry preparation declined");
            return;
        }

        _historyRetryUsed = true;
        SetHistoryRetrySuggestion(null);
        ShowSupportBundleAction = false;
        LogInfo("History retry preparation accepted: adapter=" + adapter.Name +
            " targetLocal=" + history.LocalAddress + " targetBmc=" + history.BmcAddress);

        if (!await CleanupAsync())
            return;

        // Cleanup completed and the user is returning to adapter selection rather
        // than viewing the completed timeout. Start a fresh UI session so the old
        // timeout/result card cannot render beside the retry preparation controls.
        ResetSessionStateForNewFlow();
        SetEndpointProbeOutcome(null);

        if (!history.TryApplyTo(_subnetConfig))
        {
            StatusText = "❌ 无法填入上次网段";
            DetailText = "网卡已恢复，但保存的历史地址无效。请手动选择网段后重新开始。";
            BadgeState = StepState.Failed;
            BadgeText = "! 历史无效";
            ShowSupportBundleAction = true;
            return;
        }

        ClearCandidateAddress();
        _dhcpServerError = null;
        _lastDhcpWaitTimedOut = false;
        _lastTimeoutFirewallRisk = null;
        AdapterSelectionEnabled = true;
        StartButtonEnabled = true;
        IsCleanupDone = false;
        AppPhase = AppPhase.AdapterSelection;
        StatusText = "已恢复网卡并填入上次网段";
        DetailText = "已填入 " + _subnetConfig.ServerDisplay +
            "；请确认网卡后手动点击「开始」，并再次确认网络变更。";
        ActivityText = string.Empty;
        BadgeState = StepState.Pending;
        BadgeText = "等待重新开始";
        LogInfo("History retry preparation completed; returned to adapter selection with " + _subnetConfig.ServerDisplay);
    }

    private void RememberReachableBmc(IPAddress bmcAddress, BmcReachabilityResult reachability)
    {
        if (_selectedAdapter is null)
            return;

        try
        {
            var bmcMac = _dhcpServer?.LastAssignedLease?.MacAddress;
            var bmcMacText = bmcMac is null || bmcMac.Length == 0
                ? string.Empty
                : string.Join("-", bmcMac.Select(value => value.ToString("X2")));
            if (string.IsNullOrWhiteSpace(bmcMacText))
            {
                LogInfo("BMC history skipped: DHCP client MAC was unavailable.");
                return;
            }
            BmcHistoryStore.SaveReachableAddress(_selectedAdapter, _subnetConfig, bmcAddress, reachability, bmcMacText);
            LogInfo("BMC history updated: adapter=" + _selectedAdapter.Name +
                " bmc=" + bmcAddress + " local=" + _subnetConfig.ServerDisplay);
        }
        catch (Exception ex)
        {
            LogInfo("BMC history update failed: " + ex.Message);
        }
    }

    private void ResetStepsForNewFlow()
    {
        var descriptions = new[]
        {
            "检测到 Link UP 前不会修改网卡",
            "设置静态 IP 并启动 DHCP 服务",
            "等待设备通过 DHCP 请求候选管理地址",
            "检查 HTTPS (443) 和 HTTP (80)",
            "关闭 DHCP 服务并恢复本机网卡原配置"
        };

        for (var index = 0; index < Steps.Count; index++)
        {
            Steps[index].State = StepState.Pending;
            Steps[index].Description = descriptions[index];
        }
        CurrentStepIndex = 0;
        RefreshStepFlags();
    }

    public async Task<bool> CleanupAsync()
    {
        if (IsFirewallRepairBusy) return false;
        return await CleanupForFirewallRepairAsync();
    }

    private async Task<bool> CleanupForFirewallRepairAsync()
    {
        if (_isCleanupRunning)
            return false;

        _isCleanupRunning = true;
        ExitButtonEnabled = false;
        var networkStateBeforeCleanup = NetworkLifecycleState.Untouched;
        var requiresNetworkRecovery = false;
        LogInfo("Cleanup started");

        try
        {
            var pendingRecoveryTask = _pendingRecoveryTask;
            if (SessionState.Network == NetworkLifecycleState.Restoring
                && pendingRecoveryTask is { IsCompleted: false })
            {
                LogInfo("Cleanup: waiting for pending startup recovery instead of starting a second restore");
                try
                {
                    await pendingRecoveryTask;
                }
                catch (Exception ex)
                {
                    LogInfo("Cleanup: pending startup recovery failed: " + ex.Message);
                    return false;
                }
            }

            networkStateBeforeCleanup = SessionState.Network;
            requiresNetworkRecovery = SessionState.RequiresNetworkRecovery;
            if (requiresNetworkRecovery)
                SetNetworkState(NetworkLifecycleState.Restoring);

            _flowCts?.Cancel();
            await WaitForFlowToStopAsync();
            await WaitForEndpointProbeToStopAsync();

            LogInfo("Cleanup: DHCP server disposing");

            SetStep(4, StepState.Active, requiresNetworkRecovery
                ? "正在关闭 DHCP Server 并恢复原始网卡配置..."
                : "正在关闭 DHCP Server 并清理恢复会话...");
            StatusText = "正在清理并退出...";
            DetailText = requiresNetworkRecovery
                ? "请稍候，正在恢复使用工具前的网卡配置。"
                : "请稍候，正在清理当前恢复会话。";
            ActivityText = requiresNetworkRecovery
                ? GetActivityText(4, StepState.Active)
                : networkStateBeforeCleanup == NetworkLifecycleState.RecoveryPrepared
                    ? "正在关闭 DHCP 服务并清理恢复会话，网卡尚未被修改。"
                    : "正在安全退出，网卡尚未被修改。";
            BadgeState = StepState.Active;
            BadgeText = "处理中";
            StartEllipsis();

            _dhcpServer?.Stop();
            if (_dhcpServer is not null)
            {
                _dhcpServer.ErrorEncountered -= OnDhcpServerError;
                _dhcpServer.Dispose();
            }
            _dhcpServer = null;
            LogInfo("Cleanup: DHCP server disposed");

            if (requiresNetworkRecovery)
            {
                if (_selectedAdapter is null || _originalConfig is null || _recoverySnapshot is null)
                    throw new InvalidOperationException("网卡恢复所需的会话信息不完整，无法确认原始配置已恢复。");

                var selectedAdapter = _selectedAdapter;
                var originalConfig = _originalConfig;
                var recoverySnapshot = _recoverySnapshot;
                var recoverySubnetConfig = _recoverySubnetConfig ?? _subnetConfig;
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                await NetworkRecoveryStore.ExecuteWithRecoveryLockAsync(async () =>
                {
                    var recoveryAdapter = await Task.Run(
                        () => NetworkConfigManager.ResolveCurrentAdapter(selectedAdapter),
                        cleanupCts.Token);
                    LogInfo("Cleanup: restoring original configuration for " + recoveryAdapter.Name);
                    await NetworkConfigManager.RestoreOriginalConfigAsync(
                        recoveryAdapter, originalConfig, recoverySubnetConfig, cleanupCts.Token);

                    if (recoverySnapshot is not null)
                    {
                        NetworkRecoveryStore.DeleteIfSessionMatches(recoverySnapshot.SessionId);
                    }
                }, cleanupCts.Token);
                LogInfo("Cleanup: original configuration restore done");

                if (_recoverySnapshot is not null)
                {
                    LogInfo("Cleanup: recovery snapshot removed");
                    _recoverySnapshot = null;
                }

                _recoverySubnetConfig = null;
                SetNetworkState(NetworkLifecycleState.ConfigurationRestored);
            }
            else if (networkStateBeforeCleanup == NetworkLifecycleState.RecoveryPrepared)
            {
                if (_recoverySnapshot is null)
                    throw new InvalidOperationException("恢复会话快照丢失，无法安全完成会话清理。");

                var recoverySnapshot = _recoverySnapshot;
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                await NetworkRecoveryStore.ExecuteWithRecoveryLockAsync(() =>
                {
                    NetworkRecoveryStore.DeleteIfSessionMatches(recoverySnapshot.SessionId);
                    return Task.CompletedTask;
                }, cleanupCts.Token);
                _recoverySnapshot = null;
                _recoverySubnetConfig = null;
                SetNetworkState(NetworkLifecycleState.Untouched);
                LogInfo("Cleanup: recovery session removed without network restore");
            }
            else
            {
                LogInfo("Cleanup: no network recovery or session cleanup was required");
            }

            SetStep(4, StepState.Done, requiresNetworkRecovery
                ? "✅ 原始网卡配置已恢复，DHCP Server 已关闭"
                : networkStateBeforeCleanup == NetworkLifecycleState.RecoveryPrepared
                    ? "✅ 恢复会话已清理，网卡未被修改"
                    : "✅ 未修改网卡，已安全退出");
            StatusText = "✅ 清理完成";
            DetailText = requiresNetworkRecovery
                ? "使用工具前的网卡配置已经恢复，可以安全退出。"
                : networkStateBeforeCleanup == NetworkLifecycleState.RecoveryPrepared
                    ? "恢复快照已清理，未执行网卡配置恢复。"
                    : "尚未修改本机网卡，可以安全退出。";
            ActivityText = requiresNetworkRecovery
                ? GetActivityText(4, StepState.Done)
                : networkStateBeforeCleanup == NetworkLifecycleState.RecoveryPrepared
                    ? "恢复会话已清理，网卡未被修改。"
                    : "未修改网卡，可以安全退出。";
            BadgeState = StepState.Done;
            BadgeText = "✓ 已完成";
            StopEllipsis();
            IsCleanupDone = true;
            LogInfo("Cleanup success");
            return true;
        }
        catch (Exception ex)
        {
            LogInfo("Cleanup failed: " + ex.Message);
            var restoreFailed = requiresNetworkRecovery || SessionState.Network == NetworkLifecycleState.Restoring;
            if (restoreFailed)
            {
                SetNetworkState(NetworkLifecycleState.RestoreFailed);
                SetFailure(FailureKind.RestoreFailed, ex.Message);
            }
            else if (!SessionState.HasFailure)
            {
                SetFailure(FailureKind.Unexpected, ex.Message);
            }

            SetStep(4, StepState.Failed, restoreFailed
                ? "❌ 恢复原始网卡配置时遇到问题：" + ex.Message
                : "❌ 清理恢复会话时遇到问题：" + ex.Message);
            StatusText = "❌ 清理失败，未退出";
            DetailText = restoreFailed
                ? "网卡可能尚未恢复到使用工具前的状态。请检查网络设置，或再次使用底部退出操作重试。"
                : "恢复会话尚未清理完成。请再次使用底部退出操作重试。";
            ActivityText = restoreFailed
                ? "DHCP Server 已尝试关闭，但原始网卡配置尚未确认恢复。"
                : "网卡未进入恢复路径，但恢复会话尚未确认清理。";
            BadgeState = StepState.Failed;
            BadgeText = "! 失败";
            StopEllipsis();
            IsCleanupDone = false;
            return false;
        }
        finally
        {
            _isCleanupRunning = false;
            ExitButtonEnabled = true;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Copy BMC URL
    // ════════════════════════════════════════════════════════════════

    private void CopyIp()
    {
        if (!SessionState.HasCandidateAddress)
            return;

        Clipboard.SetText(DiscoveredIpUrl);
        CopyButtonText = "已复制 ✓";

        _copyFeedbackTimer?.Stop();
        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            CopyButtonText = "复制地址";
            _copyFeedbackTimer?.Stop();
            _copyFeedbackTimer = null;
        };
        _copyFeedbackTimer.Start();
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private void SetStep(int index, StepState state, string description)
    {
        Steps[index].State = state;
        Steps[index].Description = description;
        RefreshStepFlags();
        CurrentStepIndex = index;
        ActivityText = GetActivityText(index, state);
        BadgeState = state;
        BadgeText = state switch
        {
            StepState.Done => "✓ 已完成",
            StepState.Active => "处理中",
            StepState.Failed => "! 失败",
            _ => "等待中"
        };

        OnPropertyChanged(nameof(CurrentStep));
    }

    private void SetBusy(string main, string detail)
    {
        StatusText = main;
        DetailText = detail;
    }

    private void ResetSessionStateForNewFlow()
    {
        ResetSessionStateForNewFlow(SessionState);
        NotifySessionStateChanged();
    }

    internal static void ResetSessionStateForNewFlow(CurrentSessionState sessionState)
    {
        ArgumentNullException.ThrowIfNull(sessionState);
        sessionState.Workflow = DiscoveryWorkflowState.Ready;
        sessionState.Network = NetworkLifecycleState.Untouched;
        sessionState.DhcpEvidence = DhcpEvidenceState.None;
        sessionState.BrowserLaunchResult = BrowserLaunchResult.NotRequested;
        sessionState.ClearCandidateAddress();
        sessionState.ClearFailure();
    }

    private void MarkFlowCancelled()
    {
        StopDhcpElapsedTimer(reset: true);
        ApplyFlowCancellation(SessionState);
        NotifySessionStateChanged();
    }

    internal static void ApplyFlowCancellation(CurrentSessionState sessionState)
    {
        ArgumentNullException.ThrowIfNull(sessionState);
        sessionState.ClearFailure();
        sessionState.Workflow = DiscoveryWorkflowState.Cancelled;
    }

    private void SetWorkflowState(DiscoveryWorkflowState workflow)
    {
        if (SessionState.Workflow == workflow)
            return;

        SessionState.Workflow = workflow;
        NotifySessionStateChanged();
    }

    private void SetNetworkState(NetworkLifecycleState network)
    {
        if (SessionState.Network == network)
            return;

        SessionState.Network = network;
        NotifySessionStateChanged();
    }

    // The UI observes SessionState through NotifySessionStateChanged. Record the coupled
    // ACK and candidate facts before that notification so it never observes AckSent alone.
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
        if (!SessionState.HasCandidateAddress)
            return;

        SessionState.ClearCandidateAddress();
        NotifySessionStateChanged();
    }

    private void SetFailure(FailureKind kind, string? technicalDetail = null)
    {
        SessionState.SetFailure(kind, technicalDetail);
        NotifySessionStateChanged();
    }

    // Retrying a failed probe clears its prior probe error and re-enters the running
    // workflow as one observable update; it must not expose Failed with no Failure.
    private void BeginEndpointProbe()
    {
        StopDhcpElapsedTimer(reset: true);
        if (SessionState.Failure?.Kind == FailureKind.EndpointProbeError
            || SessionState.Failure?.Kind == FailureKind.EndpointAdapterUnavailable)
            SessionState.ClearFailure();
        SessionState.Workflow = DiscoveryWorkflowState.ProbingEndpoint;
        NotifySessionStateChanged();
    }

    private void FailSession(FailureKind kind, string technicalDetail)
    {
        SetFailure(kind, technicalDetail);
        SetWorkflowState(DiscoveryWorkflowState.Failed);
    }

    private void SetBrowserLaunchResult(BrowserLaunchResult result)
    {
        if (SessionState.BrowserLaunchResult == result)
            return;

        SessionState.BrowserLaunchResult = result;
        NotifySessionStateChanged();
    }

    private bool UsesSessionPresentation =>
        SessionState.Network == NetworkLifecycleState.Restoring
        || SessionState.Network == NetworkLifecycleState.RestoreFailed
        || SessionState.Workflow != DiscoveryWorkflowState.Ready;

    private bool IsPreferredManagementScheme =>
        string.Equals(PreferredBmcScheme, "https", StringComparison.OrdinalIgnoreCase)
        || string.Equals(PreferredBmcScheme, "http", StringComparison.OrdinalIgnoreCase);

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
        // Risk can remain unchanged while detailed rule evidence changes;
        // refresh the unified runtime projection in that case as well.
        OnPropertyChanged(nameof(ModernRuntime));
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
        OnPropertyChanged(nameof(FirewallSummary));
        OnPropertyChanged(nameof(FirewallSeverity));
        OnPropertyChanged(nameof(ShowAdapterFirewallRetry));
        OnPropertyChanged(nameof(AdapterFirewallNoticeTitle));
        OnPropertyChanged(nameof(AdapterFirewallNoticeSeverity));
        OnPropertyChanged(nameof(HasFirewallAssessment));
        OnPropertyChanged(nameof(DhcpTimeoutFollowUpText));
        OnPropertyChanged(nameof(HasDhcpTimeoutFollowUp));
        OnPropertyChanged(nameof(ShowDhcpFirewallEvidence));
        OnPropertyChanged(nameof(ModernRuntime));
    }

    private void SetStartPreflightBusy(bool value)
    {
        if (_isStartPreflightBusy == value)
            return;

        _isStartPreflightBusy = value;
        if (!value)
            _showStartPreflightOverlay = false;

        OnPropertyChanged(nameof(IsStartPreflightBusy));
        OnPropertyChanged(nameof(ShowStartPreflightOverlay));
        OnPropertyChanged(nameof(AdapterSelectionEnabled));
        OnPropertyChanged(nameof(StartButtonEnabled));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetStartPreflightStage(string value)
    {
        if (_startPreflightStageText == value)
            return;

        _startPreflightStageText = value;
        OnPropertyChanged(nameof(StartPreflightStageText));
    }

    private async Task ShowStartPreflightOverlayAfterDelayAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            if (!cancellationToken.IsCancellationRequested
                && generation == _startPreflightGeneration
                && IsStartPreflightBusy)
            {
                _showStartPreflightOverlay = true;
                OnPropertyChanged(nameof(ShowStartPreflightOverlay));
            }
        }
        catch (OperationCanceledException)
        {
            // A fast check or an explicit close cancels the delayed visual only.
        }
    }

    private void SetLongLinkWait(bool value)
    {
        if (_hasLongLinkWait == value)
            return;

        _hasLongLinkWait = value;
        OnPropertyChanged(nameof(LinkStatusText));
        OnPropertyChanged(nameof(ShowLongLinkWaitHint));
        OnPropertyChanged(nameof(ModernRuntime));
    }

    private void NotifySessionPresentationChanged()
    {
        OnPropertyChanged(nameof(CurrentSessionPresentation));
        OnPropertyChanged(nameof(CurrentSessionPage));
        OnPropertyChanged(nameof(SessionPageTitle));
        OnPropertyChanged(nameof(SessionPageSummary));
        OnPropertyChanged(nameof(RecommendedActionKind));
        OnPropertyChanged(nameof(NetworkStatusPresentation));
        OnPropertyChanged(nameof(NetworkStatusText));
        OnPropertyChanged(nameof(ModernNetworkStatusText));
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
        OnPropertyChanged(nameof(HistoryAdapterName));
        OnPropertyChanged(nameof(HistoryLocalAddress));
        OnPropertyChanged(nameof(HistoryBmcAddress));
        OnPropertyChanged(nameof(HistoryEndpointText));
        OnPropertyChanged(nameof(HistoryLastConfirmedText));
        OnPropertyChanged(nameof(ShowLegacyRuntimeProgress));
        OnPropertyChanged(nameof(ShowHistoryRetryCard));
        OnPropertyChanged(nameof(ShowLegacySupportBundleCard));
        OnPropertyChanged(nameof(ShowPreparationContent));
        OnPropertyChanged(nameof(ShowRuntimeHero));
        OnPropertyChanged(nameof(ShowModernRuntimeHost));
        OnPropertyChanged(nameof(ShowLegacySessionPages));
        OnPropertyChanged(nameof(ModernRuntime));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(ExitButtonText));
        OnPropertyChanged(nameof(IsExitActionEnabled));
    }

    private void NotifySessionStateChanged()
    {
        OnPropertyChanged(nameof(FirewallRepairEnabled));
        OnPropertyChanged(nameof(ShowFirewallRepair));
        OnPropertyChanged(nameof(SessionState));
        OnPropertyChanged(nameof(DiscoveredIp));
        OnPropertyChanged(nameof(IsIpDiscovered));
        OnPropertyChanged(nameof(IsIpCardVisible));
        OnPropertyChanged(nameof(DiscoveredIpUrl));
        NotifySessionPresentationChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private static void LogInfo(string message) => AppLogger.Log(message);

    private bool RequestConsent(ConsentNotice notice)
    {
        var presenter = ConsentRequested;
        if (presenter is null)
        {
            LogInfo("Consent blocked because no presenter is registered: " + notice.Title);
            return false;
        }

        try
        {
            return presenter.Invoke(notice);
        }
        catch (Exception ex)
        {
            LogInfo("Consent dialog failed: " + ex.Message);
            StatusText = "❌ 无法显示风险告知";
            DetailText = "为避免误操作，已停止继续：" + ex.Message;
            BadgeState = StepState.Failed;
            BadgeText = "! 无法确认";
            return false;
        }
    }

    public void CancelFlow()
    {
        if (IsFirewallRepairBusy) return;
        LogInfo("Cancel requested");
        _startPreflightCts?.Cancel();
        if (SessionState.Workflow == DiscoveryWorkflowState.WaitingForLink
            || SessionState.Workflow == DiscoveryWorkflowState.ConfiguringNetwork
            || SessionState.Workflow == DiscoveryWorkflowState.WaitingForDhcp
            || SessionState.Workflow == DiscoveryWorkflowState.ProbingEndpoint)
        {
            MarkFlowCancelled();
        }
        _flowCts?.Cancel();
    }

    private string GetActivityText(int index, StepState state)
    {
        if (state == StepState.Done)
        {
            return index switch
            {
                0 => "已检测到网线连接，链路已 UP。",
                1 => "本机网卡和 DHCP 服务已准备好。",
                2 => "已取得候选管理地址。",
                3 => "已完成地址可达性确认。",
                _ => "原始网卡配置已恢复，可以安全退出。"
            };
        }

        if (state == StepState.Failed)
        {
            return "遇到问题，请按提示处理；退出时会尝试恢复原始网卡配置。";
        }

        if (state == StepState.Pending)
        {
            return index switch
            {
                4 => "完成浏览器中的操作后，使用右下角操作恢复本机网卡原配置。",
                _ => "等待上一步完成。"
            };
        }

        return index switch
        {
            0 => "正在等待你插入连接服务器管理口的网线；此时不会修改网卡...",
            1 => "正在将网卡设置为静态 IP " + _subnetConfig.ServerDisplay + "，请稍候...",
            2 => "正在等待设备通过 DHCP 请求候选管理地址...",
            3 => "正在并行检测候选地址的 Ping、TCP 443 和 TCP 80...",
            _ => "正在关闭 DHCP 服务并恢复原始网卡配置..."
        };
    }

    private async Task WaitForFlowToStopAsync()
    {
        var flowTask = _flowTask;
        if (flowTask is null || flowTask.IsCompleted)
            return;

        try
        {
            await flowTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WaitForEndpointProbeToStopAsync()
    {
        var probeTask = _endpointProbeTask;
        if (probeTask is null || probeTask.IsCompleted)
            return;

        try
        {
            await probeTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void MarkCurrentFailure(string message)
    {
        var index = -1;
        for (int i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].State == StepState.Active) { index = i; break; }
            if (index < 0 && Steps[i].State == StepState.Pending) index = i;
        }

        if (index >= 0)
            SetStep(index, StepState.Failed, "❌ " + message);
    }

    private static string BuildFailureDetail(int stepIndex, string message)
    {
        return stepIndex switch
        {
            0 => "未检测到网线连接。请确认网线直连服务器独立管理口，而非普通业务网口或交换机口。原始错误：" + message,
            1 => "配置本机网卡失败。请确认已用管理员权限运行，并检查安全软件是否拦截网络配置。原始错误：" + message,
            2 => message.StartsWith("应用层在等待期内", StringComparison.Ordinal)
                ? message
                : "等待 DHCP 地址分配流程时遇到问题。请检查防火墙、管理口网络模式和管理口连接。原始错误：" + message,
            3 => "已取得候选管理地址，但可达性检测时遇到问题。可以重新检测或手动访问 HTTP / HTTPS。原始错误：" + message,
            4 => "恢复原始网卡配置时失败。请再次点击「恢复网卡并退出」，或手动检查网卡 IPv4 设置。原始错误：" + message,
            _ => message
        };
    }

    private async Task<FirewallAssessment> AssessFirewallAsync(
        WiredAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        var assessment = await FirewallAssessmentService.AssessAsync(
            adapter.Name,
            adapter.Id,
            adapter.MacAddress,
            FirewallAssessmentService.GetCurrentExecutablePath(),
            _firewallNetworkCategorySnapshot,
            cancellationToken);
        AppLogger.Log("Firewall assessment: adapter=" + adapter.Name +
            " interfaceIndex=" + (assessment.InterfaceIndex?.ToString() ?? "unknown") +
            " category=" + assessment.NetworkCategory +
            " observedCategory=" + assessment.ObservedNetworkCategory +
            " categorySource=" + assessment.NetworkCategorySource +
            " enabled=" + (assessment.SelectedFirewallEnabled?.ToString() ?? "unknown") +
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
        if (assessment is null || assessment.UsesNetworkCategorySnapshot ||
            string.Equals(assessment.NetworkCategory, "Unknown", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(assessment.NetworkCategorySource, "Selected adapter (Get-NetConnectionProfile)", StringComparison.Ordinal))
            return;

        _firewallNetworkCategorySnapshot = assessment.NetworkCategory;
        LogInfo("Firewall profile snapshot captured before adapter configuration: " +
            _firewallNetworkCategorySnapshot);
    }

    // ════════════════════════════════════════════════════════════════
    //  DHCP elapsed display (presentation only)
    // ════════════════════════════════════════════════════════════════

    private void StartDhcpElapsedTimer()
    {
        StopDhcpElapsedTimer(reset: true);
        _dhcpWaitStopwatch = Stopwatch.StartNew();
        _dhcpWaitStartedUtc = DateTime.UtcNow;
        _lastDhcpTimerTickUtc = DateTime.UtcNow;
        _dhcpTimerLagLogged = false;
        UpdateDhcpElapsedDisplay();

        _dhcpElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dhcpElapsedTimer.Tick += OnDhcpElapsedTimerTick;
        _dhcpElapsedTimer.Start();
    }

    private void OnDhcpElapsedTimerTick(object? sender, EventArgs e)
    {
        UpdateDhcpElapsedDisplay();
    }

    private void StopDhcpElapsedTimer(bool reset)
    {
        if (_dhcpElapsedTimer is not null)
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
            OnPropertyChanged(nameof(ModernRuntime));
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

        var elapsed = _dhcpWaitStopwatch?.Elapsed
            ?? (DateTime.UtcNow - _dhcpWaitStartedUtc.Value);
        var now = DateTime.UtcNow;
        if (_lastDhcpTimerTickUtc.HasValue)
        {
            var gap = now - _lastDhcpTimerTickUtc.Value;
            if (gap >= TimeSpan.FromSeconds(2.5) && !_dhcpTimerLagLogged)
            {
                _dhcpTimerLagLogged = true;
                LogInfo("DHCP UI timer delayed: gapMs=" + gap.TotalMilliseconds.ToString("0"));
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
        OnPropertyChanged(nameof(ModernRuntime));
    }

    internal static string FormatDhcpElapsed(TimeSpan elapsed)
    {
        var totalSeconds = (int)Math.Floor(Math.Max(0, Math.Min(180, elapsed.TotalSeconds)));
        return (totalSeconds / 60).ToString("00") + ":" + (totalSeconds % 60).ToString("00");
    }

    // ════════════════════════════════════════════════════════════════
    //  Ellipsis animation
    // ════════════════════════════════════════════════════════════════

    private void StartEllipsis()
    {
        StopEllipsis();
        _ellipsisDots = 0;
        _ellipsisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _ellipsisTimer.Tick += (_, _) =>
        {
            _ellipsisDots = (_ellipsisDots + 1) % 4;
            var dots = new string('.', _ellipsisDots);
            BadgeText = "处理中" + dots;
        };
        _ellipsisTimer.Start();
    }

    private void StopEllipsis()
    {
        _ellipsisTimer?.Stop();
        _ellipsisTimer = null;
    }

    // ════════════════════════════════════════════════════════════════
    //  INotifyPropertyChanged
    // ════════════════════════════════════════════════════════════════

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Simple reusable ICommand implementation.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task>? _executeAsync;
    private readonly Action<object?>? _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter)
        => _canExecute?.Invoke(parameter) ?? true;

    public async void Execute(object? parameter)
    {
        if (_executeAsync is not null)
            await _executeAsync(parameter);
        else
            _execute?.Invoke(parameter);
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
}
