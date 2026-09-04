using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace EzGetBmcIp;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SubnetConfig _subnetConfig = new();
    private DhcpServer? _dhcpServer;
    private CancellationTokenSource? _flowCts;
    private Task? _flowTask;
    private Task? _endpointProbeTask;
    private WiredAdapter? _selectedAdapter;
    private AdapterOriginalConfig? _originalConfig;
    private NetworkRecoverySnapshot? _recoverySnapshot;
    private string? _dhcpServerError;
    private DispatcherTimer? _ellipsisTimer;
    private int _ellipsisDots;
    private bool _isCleanupRunning;
    private bool _adapterMutationStarted;

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
        }
    }

    public bool IsAdapterVisible => _appPhase != AppPhase.Preparation;

    public SubnetConfig SubnetConfig => _subnetConfig;
    public string DhcpListenerStatus => _dhcpServer?.BindingDescription ?? "(not running)";

    // ════════════════════════════════════════════════════════════════
    //  Steps
    // ════════════════════════════════════════════════════════════════

    public ObservableCollection<StepItem> Steps { get; } = new()
    {
        new StepItem("1. 连接 IPMI 管理口", "连接网线", "检测到 Link UP 前不会修改网卡"),
        new StepItem("2. 配置本机网卡", "配置网卡", "设置静态 IP 并启动 DHCP 服务"),
        new StepItem("3. 自动获取 IPMI 地址", "获取 IP", "等待 IPMI 通过 DHCP 获取地址"),
        new StepItem("4. 打开 BMC 页面", "打开页面", "调用浏览器打开管理页面"),
        new StepItem("5. 完成后退出", "清理退出", "关闭 DHCP 服务并恢复原始网卡配置")
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
        set { _currentStepIndex = value; OnPropertyChanged(); OnPropertyChanged(nameof(CurrentStep)); }
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
        set { _selectedAdapterItem = value; OnPropertyChanged(); }
    }

    // ════════════════════════════════════════════════════════════════
    //  UI bindings
    // ════════════════════════════════════════════════════════════════

    private string _statusText = "欢迎使用 IPMI/BMC 直连助手";

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _detailText = "直连服务器管理口，自动获取 BMC 地址。";

    public string DetailText
    {
        get => _detailText;
        set { _detailText = value; OnPropertyChanged(); }
    }

    private string? _initError;

    private string _activityText = "";

    public string ActivityText
    {
        get => _activityText;
        set { _activityText = value; OnPropertyChanged(); }
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
        get => _adapterSelectionEnabled;
        set { _adapterSelectionEnabled = value; OnPropertyChanged(); }
    }

    private bool _startButtonEnabled = true;

    public bool StartButtonEnabled
    {
        get => _startButtonEnabled;
        set { _startButtonEnabled = value; OnPropertyChanged(); }
    }

    private bool _exitButtonEnabled = true;

    public bool ExitButtonEnabled
    {
        get => _exitButtonEnabled;
        set { _exitButtonEnabled = value; OnPropertyChanged(); }
    }

    private bool _isCleanupDone;

    public bool IsCleanupDone
    {
        get => _isCleanupDone;
        set { _isCleanupDone = value; OnPropertyChanged(); }
    }

    private string? _discoveredIp;

    public string? DiscoveredIp
    {
        get => _discoveredIp;
        set
        {
            _discoveredIp = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsIpDiscovered));
            OnPropertyChanged(nameof(IsIpCardVisible));
            OnPropertyChanged(nameof(DiscoveredIpUrl));
            OnPropertyChanged(nameof(ExitButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsIpDiscovered => !string.IsNullOrEmpty(_discoveredIp);

    public bool IsIpCardVisible => IsIpDiscovered && _appPhase == AppPhase.FlowRunning;

    public string DiscoveredIpUrl => string.IsNullOrWhiteSpace(_discoveredIp)
        ? ""
        : PreferredBmcScheme + "://" + _discoveredIp;

    private string _preferredBmcScheme = "https";

    public string PreferredBmcScheme
    {
        get => _preferredBmcScheme;
        private set
        {
            _preferredBmcScheme = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiscoveredIpUrl));
        }
    }

    private string _endpointStatusText = "等待 BMC 管理页面响应。";

    public string EndpointStatusText
    {
        get => _endpointStatusText;
        set { _endpointStatusText = value; OnPropertyChanged(); }
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
    public string ExitButtonText => IsIpDiscovered ? "恢复网卡并退出" : "完成 / 退出";

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

    // ════════════════════════════════════════════════════════════════
    //  Events (for window interaction)
    // ════════════════════════════════════════════════════════════════

    public event Action? RequestClose;
    public event Action<string>? OpenBrowserRequested;
    internal event Func<ConsentNotice, bool>? ConsentRequested;

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
        }, _ => IsIpDiscovered && !IsEndpointProbeRunning);
        OpenHttpsCommand = new RelayCommand(_ => OpenBrowserForScheme("https"), _ => IsIpDiscovered);
        OpenHttpCommand = new RelayCommand(_ => OpenBrowserForScheme("http"), _ => IsIpDiscovered);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            LogInfo("Adapter enumeration started");
            var adapters = await Task.Run(NetworkConfigManager.GetWiredAdapters);
            LogInfo("Adapter enumeration done: " + adapters.Count + " adapter(s) found");

            await RecoverPendingNetworkConfigurationAsync(adapters);

            if (adapters.Count == 0)
                throw new InvalidOperationException("未检测到可用网卡，请确认网卡驱动已安装。");

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

                var adapter = NetworkConfigManager.ResolveCurrentAdapter(currentSnapshot.ToAdapter());
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
        }
        catch (Exception ex)
        {
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

        if (!RequestConsent(ConsentNotice.CreateUsageRisk()))
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
        if (SelectedAdapterItem is null)
            return;

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

        AdapterOriginalConfig originalConfig;
        try
        {
            originalConfig = NetworkConfigManager.CaptureOriginalConfig(SelectedAdapterItem);
        }
        catch (Exception ex)
        {
            LogInfo("Original configuration capture failed before consent: " + ex.Message);
            StatusText = "❌ 无法读取网卡原始配置";
            DetailText = "为避免覆盖现有网络设置，已停止操作：" + ex.Message;
            BadgeState = StepState.Failed;
            BadgeText = "! 无法确认";
            return;
        }

        var firewallAssessment = await AssessFirewallAsync(SelectedAdapterItem);
        if (!RequestConsent(ConsentNotice.CreateNetworkChange(
                SelectedAdapterItem, _subnetConfig, originalConfig, firewallAssessment)))
        {
            LogInfo("Network change consent declined");
            return;
        }

        _selectedAdapter = SelectedAdapterItem;
        _originalConfig = originalConfig;
        _adapterMutationStarted = false;
        _recoverySnapshot = null;
        AdapterSelectionEnabled = false;
        StartButtonEnabled = false;
        AppPhase = AppPhase.FlowRunning;
        DiscoveredIp = null;
        PreferredBmcScheme = "https";
        EndpointStatusText = "等待 BMC 管理页面响应。";
        CopyButtonText = "复制地址";
        AdapterCardLine1 = "✓ " + _selectedAdapter.DisplayName;
        AdapterCardLine2 = "";
        _flowCts = new CancellationTokenSource();
        _dhcpServerError = null;

        LogInfo("Flow started: adapter=" + _selectedAdapter.Name + " subnet=" + _subnetConfig.ServerDisplay + " pool=" + _subnetConfig.PoolStart);

        try
        {
            await RunLinkThenConfigureAsync(
                WaitForLinkAsync,
                ConfigureLocalAdapterAsync,
                _flowCts.Token);
            var lease = await WaitForDhcpLeaseAsync(_flowCts.Token);
            DiscoveredIp = lease.IpAddress.ToString();
            LogInfo("Flow: BMC IP discovered " + _discoveredIp);
            await ProbeBmcEndpointAsync(lease.IpAddress, autoOpen: true, _flowCts.Token);
        }
        catch (OperationCanceledException)
        {
            LogInfo("Flow cancelled");
            StatusText = "正在退出...";
            DetailText = "正在恢复使用工具前的网卡配置。";
            StopEllipsis();
        }
        catch (Exception ex)
        {
            LogInfo("Flow failed: " + ex.Message);
            var failureDetail = BuildFailureDetail(CurrentStepIndex, ex.Message);
            if (_adapterMutationStarted)
            {
                failureDetail += " 网卡已经开始修改，请先点击「完成 / 退出」执行恢复；恢复成功前不能重新开始。";
            }
            MarkCurrentFailure(failureDetail);
            StatusText = "❌ 操作失败";
            DetailText = failureDetail;
            AdapterSelectionEnabled = !_adapterMutationStarted;
            StartButtonEnabled = !_adapterMutationStarted;
            BadgeState = StepState.Failed;
            BadgeText = "! 失败";
            StopEllipsis();
        }
    }

    private async Task ConfigureLocalAdapterAsync(CancellationToken ct)
    {
        if (_selectedAdapter is null || _originalConfig is null)
            throw new InvalidOperationException("未确认网卡原始配置，已停止修改网络设置。");

        _selectedAdapter = NetworkConfigManager.ResolveCurrentAdapter(_selectedAdapter);
        _originalConfig = NetworkConfigManager.CaptureOriginalConfig(_selectedAdapter);

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

        _adapterMutationStarted = true;
        LogInfo("Adapter mutation started after Link UP");
        await NetworkConfigManager.SetStaticForToolAsync(_selectedAdapter, _subnetConfig, ct);
        LogInfo("Static IP set: " + _subnetConfig.ServerDisplay);
        _dhcpServer = new DhcpServer(_subnetConfig, _selectedAdapter);
        _dhcpServer.Logger = msg => LogInfo("[DHCP] " + msg);
        _dhcpServer.ErrorEncountered += OnDhcpServerError;
        _dhcpServer.Start();
        await CompleteStepAsync(1, "✅ 本机网卡配置完成：" + _subnetConfig.ServerDisplay, ct);
        StopEllipsis();
    }

    private void OnDhcpServerError(object? sender, string message)
    {
        _dhcpServerError = message;
        LogInfo("[DHCP] Error: " + message);
    }

    private async Task WaitForLinkAsync(CancellationToken ct)
    {
        LogInfo("Link wait started");
        SetStep(0, StepState.Active, "请用网线连接服务器的 IPMI 管理口，正在等待 Link UP。");
        SetBusy("请插入网线", "等待检测到网线连接，预计几秒内完成。");
        StartEllipsis();

        var warned = false;
        var startTime = DateTime.UtcNow;

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
                DetailText = "⚠ 已等待 60 秒仍未检测到网线连接，请确认网线已直连服务器 IPMI 管理口。";
                warned = true;
            }

            await Task.Delay(1500, ct);
        }
    }

    private async Task<DhcpLease> WaitForDhcpLeaseAsync(CancellationToken ct)
    {
        LogInfo("DHCP lease wait started");
        SetStep(2, StepState.Active, "链路已 UP，正在等待 IPMI 通过 DHCP 获取地址，最多等待 3 分钟。");
        SetBusy("正在等待 IPMI 获取 IP...", "如果 3 分钟内没有完成，请检查 BMC 是否使用固定 IP、是否允许当前程序通过防火墙，以及网线是否连接管理口。");
        StartEllipsis();

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
        try
        {
            if (!string.IsNullOrWhiteSpace(_dhcpServerError))
                throw new InvalidOperationException(_dhcpServerError);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

            var lease = await tcs.Task;
            LogInfo("DHCP lease acquired: IP=" + lease.IpAddress + " MAC=" + (lease.MacAddress.Length > 0 ? string.Join("-", lease.MacAddress.Select(b => b.ToString("X2"))) : "none"));
            await CompleteStepAsync(2, "✅ 已向直连设备分配候选地址：" + lease.IpAddress, ct);
            StopEllipsis();
            return lease;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var adapter = _selectedAdapter ?? SelectedAdapterItem;
            var firewallAssessment = adapter is null
                ? FirewallAssessmentService.CreateUnknown(
                    FirewallAssessmentService.GetCurrentExecutablePath(),
                    "No selected adapter was available for the timeout assessment.")
                : await AssessFirewallAsync(adapter);
            throw new TimeoutException(firewallAssessment.BuildTimeoutGuidance());
        }
        finally
        {
            if (_dhcpServer is not null)
            {
                _dhcpServer.LeaseAssigned -= Handler;
                _dhcpServer.ErrorEncountered -= ErrorHandler;
            }
        }
    }

    private async Task RetryEndpointProbeAsync()
    {
        if (!IPAddress.TryParse(DiscoveredIp, out var ipAddress))
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
            EndpointStatusText = "重新检测管理页面时遇到问题：" + ex.Message;
        }
    }

    internal static async Task RunLinkThenConfigureAsync(
        Func<CancellationToken, Task> waitForLink,
        Func<CancellationToken, Task> configureAdapter,
        CancellationToken cancellationToken)
    {
        await waitForLink(cancellationToken);
        await configureAdapter(cancellationToken);
    }

    internal static bool ShouldRestoreAdapter(bool adapterMutationStarted)
    {
        return adapterMutationStarted;
    }

    private async Task<bool> ProbeBmcEndpointAsync(
        IPAddress ipAddress,
        bool autoOpen,
        CancellationToken cancellationToken)
    {
        IsEndpointProbeRunning = true;
        EndpointStatusText = "地址已分配，正在检测 HTTPS 和 HTTP 管理页面...";
        SetStep(3, StepState.Active, "BMC 已获得地址，正在确认管理页面是否可以访问。");
        SetBusy("BMC 地址已分配，正在等待管理页面...", "地址：" + ipAddress + "；优先检测 HTTPS。 ");
        StartEllipsis();

        try
        {
            var endpoint = await BmcEndpointProbe.WaitForEndpointAsync(
                ipAddress,
                TimeSpan.FromSeconds(45),
                cancellationToken,
                message => LogInfo("[Probe] " + message));

            if (endpoint is null)
            {
                PreferredBmcScheme = "https";
                EndpointStatusText = "候选地址已分配，但 45 秒内管理页面尚未响应。可以重新检测，或手动尝试 HTTPS / HTTP。";
                SetStep(3, StepState.Pending, "⚠ 已分配候选地址，但尚未确认 BMC 管理页面。");
                StatusText = "已获取候选地址，尚未确认 BMC 页面";
                DetailText = "设备可能仍在启动，也可能不是 BMC。可以点击「重新检测」，或手动尝试 HTTPS / HTTP。";
                BadgeState = StepState.Pending;
                BadgeText = "等待页面";
                ActivityText = "DHCP 地址分配已完成，管理页面仍在启动或使用了其他端口。";
                StopEllipsis();
                return false;
            }

            PreferredBmcScheme = endpoint.Scheme;
            EndpointStatusText = endpoint.Scheme == "https"
                ? "已确认 HTTPS 管理页面可访问。"
                : "已确认 HTTP 管理页面可访问。";
            LogInfo("BMC endpoint confirmed: " + endpoint.Url);

            if (autoOpen)
                OpenBrowser(endpoint.Url);

            await CompleteStepAsync(3, "✅ 已确认并打开 BMC 管理页面", cancellationToken);
            SetStep(4, StepState.Pending, "可以登录 BMC 后点击「完成 / 退出」恢复原始网卡配置。");
            StatusText = "BMC 页面已打开";
            DetailText = "完成查看或登录后，点击右下角恢复网卡并退出。";
            BadgeState = StepState.Done;
            BadgeText = "✓ 已完成";
            ActivityText = "BMC 管理页面已确认可访问，完成后点击右下角恢复网卡。";
            StopEllipsis();
            return true;
        }
        finally
        {
            IsEndpointProbeRunning = false;
        }
    }

    private void OpenBrowserForScheme(string scheme)
    {
        if (!string.IsNullOrWhiteSpace(DiscoveredIp))
            OpenBrowser(scheme + "://" + DiscoveredIp);
    }

    private void OpenBrowser(string url)
    {
        try
        {
            OpenBrowserRequested?.Invoke(url);
            LogInfo("Browser opened for " + url);
        }
        catch (Exception ex)
        {
            LogInfo("Browser open failed: " + ex.Message);
            throw;
        }
    }

    private async Task CompleteStepAsync(int index, string description, CancellationToken ct)
    {
        SetStep(index, StepState.Done, description);
        await Task.Delay(1500, ct);
    }

    public async Task<bool> CleanupAsync()
    {
        if (_isCleanupRunning)
            return false;

        _isCleanupRunning = true;
        ExitButtonEnabled = false;
        var adapterWasModified = _adapterMutationStarted;
        LogInfo("Cleanup started");

        try
        {
            _flowCts?.Cancel();
            await WaitForFlowToStopAsync();
            await WaitForEndpointProbeToStopAsync();

            LogInfo("Cleanup: DHCP server disposing");

            SetStep(4, StepState.Active, "正在关闭 DHCP Server 并恢复原始网卡配置...");
            StatusText = "正在清理并退出...";
            DetailText = "请稍候，正在恢复使用工具前的网卡配置。";
            ActivityText = GetActivityText(4, StepState.Active);
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

            if (ShouldRestoreAdapter(_adapterMutationStarted)
                && _selectedAdapter is not null
                && _originalConfig is not null)
            {
                var selectedAdapter = _selectedAdapter;
                var originalConfig = _originalConfig;
                var recoverySnapshot = _recoverySnapshot;
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                await NetworkRecoveryStore.ExecuteWithRecoveryLockAsync(async () =>
                {
                    var recoveryAdapter = NetworkConfigManager.ResolveCurrentAdapter(selectedAdapter);
                    LogInfo("Cleanup: restoring original configuration for " + recoveryAdapter.Name);
                    await NetworkConfigManager.RestoreOriginalConfigAsync(
                        recoveryAdapter, originalConfig, _subnetConfig, cleanupCts.Token);

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

                _adapterMutationStarted = false;
            }
            else
            {
                LogInfo("Cleanup: adapter was not modified; restore skipped");
            }

            SetStep(4, StepState.Done, adapterWasModified
                ? "✅ 原始网卡配置已恢复，DHCP Server 已关闭"
                : "✅ 未修改网卡，已安全退出");
            StatusText = "✅ 清理完成";
            DetailText = adapterWasModified
                ? "使用工具前的网卡配置已经恢复，可以安全退出。"
                : "尚未修改本机网卡，可以安全退出。";
            ActivityText = GetActivityText(4, StepState.Done);
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
            SetStep(4, StepState.Failed, "❌ 恢复原始网卡配置时遇到问题：" + ex.Message);
            StatusText = "❌ 清理失败，未退出";
            DetailText = "网卡可能尚未恢复到使用工具前的状态。请检查网络设置，或再次点击「完成 / 退出」重试。";
            ActivityText = "DHCP Server 已尝试关闭，但原始网卡配置尚未确认恢复。";
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
        if (string.IsNullOrEmpty(_discoveredIp))
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
        LogInfo("Cancel requested");
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
                2 => "已获取 IPMI 设备地址。",
                3 => "BMC 管理页面已打开。",
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
                4 => "完成登录后点击右下角按钮退出并恢复原始网卡配置。",
                _ => "等待上一步完成。"
            };
        }

        return index switch
        {
            0 => "正在等待你插入连接 IPMI 管理口的网线；此时不会修改网卡...",
            1 => "正在将网卡设置为静态 IP " + _subnetConfig.ServerDisplay + "，请稍候...",
            2 => "正在等待 IPMI 设备通过 DHCP 获取地址...",
            3 => "正在打开默认浏览器访问 BMC 管理页面...",
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
            0 => "未检测到网线连接。请确认网线直连服务器 IPMI/BMC 管理口，不是普通业务网口或交换机口。原始错误：" + message,
            1 => "配置本机网卡失败。请确认已用管理员权限运行，并检查安全软件是否拦截网络配置。原始错误：" + message,
            2 => message.StartsWith("应用层在等待期内", StringComparison.Ordinal)
                ? message
                : "等待 BMC DHCP 流程时遇到问题。请检查防火墙、BMC 网络模式和管理口连接。原始错误：" + message,
            3 => "已分配 BMC 地址，但确认或打开管理页面时遇到问题。可以重新检测或手动访问。原始错误：" + message,
            4 => "恢复原始网卡配置时失败。请再次点击「恢复网卡并退出」，或手动检查网卡 IPv4 设置。原始错误：" + message,
            _ => message
        };
    }

    private static async Task<FirewallAssessment> AssessFirewallAsync(WiredAdapter adapter)
    {
        var assessment = await FirewallAssessmentService.AssessAsync(
            adapter.Name,
            adapter.Id,
            adapter.MacAddress,
            FirewallAssessmentService.GetCurrentExecutablePath());
        AppLogger.Log("Firewall assessment: adapter=" + adapter.Name +
            " interfaceIndex=" + (assessment.InterfaceIndex?.ToString() ?? "unknown") +
            " category=" + assessment.NetworkCategory +
            " enabled=" + (assessment.SelectedFirewallEnabled?.ToString() ?? "unknown") +
            " risk=" + assessment.RiskLevel +
            " appAllow=" + assessment.HasMatchingProgramAllow +
            " appBlock=" + assessment.HasMatchingProgramBlock +
            " portAllow=" + assessment.HasMatchingPortAllow +
            " portBlock=" + assessment.HasMatchingPortBlock +
            (string.IsNullOrWhiteSpace(assessment.Error) ? "" : " error=" + assessment.Error));
        return assessment;
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
