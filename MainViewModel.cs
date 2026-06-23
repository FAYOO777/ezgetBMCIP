using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
    private WiredAdapter? _selectedAdapter;
    private AdapterOriginalConfig? _originalConfig;
    private DispatcherTimer? _ellipsisTimer;
    private int _ellipsisDots;
    private bool _isCleanupRunning;

    private AppPhase _appPhase = AppPhase.Preparation;

    public AppPhase AppPhase
    {
        get => _appPhase;
        set { _appPhase = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAdapterVisible)); OnPropertyChanged(nameof(IsIpCardVisible)); }
    }

    public bool IsAdapterVisible => _appPhase != AppPhase.Preparation;

    public SubnetConfig SubnetConfig => _subnetConfig;

    // ════════════════════════════════════════════════════════════════
    //  Steps
    // ════════════════════════════════════════════════════════════════

    public ObservableCollection<StepItem> Steps { get; } = new()
    {
        new StepItem("1. 配置本机网卡", "配置网卡", "设置静态 IP 并启动 DHCP 服务"),
        new StepItem("2. 连接 IPMI 管理口", "连接网线", "等待检测到网线连接"),
        new StepItem("3. 自动获取 IPMI 地址", "获取 IP", "等待 IPMI 通过 DHCP 获取地址"),
        new StepItem("4. 打开 BMC 页面", "打开页面", "调用浏览器打开管理页面"),
        new StepItem("5. 完成后退出", "清理退出", "关闭 DHCP 服务并恢复 DHCP")
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

    private string _statusText = "欢迎使用 ezgetBMCIP";

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _detailText = "直连服务器，自动分配 BMC 管理口 IP。";

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
        set { _discoveredIp = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIpDiscovered)); OnPropertyChanged(nameof(DiscoveredIpUrl)); }
    }

    public bool IsIpDiscovered => !string.IsNullOrEmpty(_discoveredIp);

    public bool IsIpCardVisible => IsIpDiscovered && _appPhase == AppPhase.FlowRunning;

    public string DiscoveredIpUrl => "http://" + _discoveredIp;

    private string _copyButtonText = "复制 IP";

    public string CopyButtonText
    {
        get => _copyButtonText;
        set { _copyButtonText = value; OnPropertyChanged(); }
    }

    private DispatcherTimer? _copyFeedbackTimer;

    public string VersionText => GetVersionText();
    public string GitHubUrl => "https://github.com/FAYOO777/ezgetBMCIP";

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

    // ════════════════════════════════════════════════════════════════
    //  Events (for window interaction)
    // ════════════════════════════════════════════════════════════════

    public event Action? RequestClose;
    public event Action<string>? OpenBrowserRequested;

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
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            LogInfo("Adapter enumeration started");
            var adapters = await Task.Run(NetworkConfigManager.GetWiredAdapters);
            LogInfo("Adapter enumeration done: " + adapters.Count + " adapter(s) found");
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

        AppPhase = AppPhase.AdapterSelection;
        StatusText = "请选择目标网卡";
        DetailText = "选择要连接服务器 IPMI 管理口的网卡，然后点击「开始」。";
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

        _selectedAdapter = SelectedAdapterItem;
        AdapterSelectionEnabled = false;
        StartButtonEnabled = false;
        AppPhase = AppPhase.FlowRunning;
        DiscoveredIp = null;
        CopyButtonText = "复制 IP";
        AdapterCardLine1 = "✓ " + _selectedAdapter.DisplayName;
        AdapterCardLine2 = "";
        _flowCts = new CancellationTokenSource();

        LogInfo("Flow started: adapter=" + _selectedAdapter.Name + " subnet=" + _subnetConfig.ServerDisplay + " pool=" + _subnetConfig.PoolStart);

        try
        {
            await ConfigureLocalAdapterAsync(_flowCts.Token);
            await WaitForLinkAsync(_flowCts.Token);
            var lease = await WaitForDhcpLeaseAsync(_flowCts.Token);
            DiscoveredIp = lease.IpAddress.ToString();
            LogInfo("Flow: BMC IP discovered " + _discoveredIp);
            SetStep(3, StepState.Active, "正在打开默认浏览器访问 BMC 管理页面。");
            SetBusy("正在打开 BMC 管理页面...", "BMC 地址：http://" + lease.IpAddress);
            OpenBrowser(lease.IpAddress.ToString());
            await CompleteStepAsync(3, "✅ 已自动打开 BMC 管理页面", _flowCts.Token);

            SetStep(4, StepState.Pending, "可以登录 BMC 后点击「完成 / 退出」恢复 DHCP。");
            StatusText = "✅ 已自动打开 BMC 管理页面";
            DetailText = "BMC 地址：http://" + lease.IpAddress;
            BadgeState = StepState.Done;
            BadgeText = "✓ 已完成";
            ActivityText = "BMC 管理页面已打开，完成后点击右下角退出。";
            StopEllipsis();
        }
        catch (OperationCanceledException)
        {
            LogInfo("Flow cancelled");
            StatusText = "正在退出...";
            DetailText = "正在恢复网卡为 DHCP。";
            StopEllipsis();
        }
        catch (Exception ex)
        {
            LogInfo("Flow failed: " + ex.Message);
            MarkCurrentFailure(ex.Message);
            StatusText = "❌ 操作失败";
            DetailText = ex.Message;
            AppPhase = AppPhase.AdapterSelection;
            AdapterSelectionEnabled = true;
            StartButtonEnabled = true;
            BadgeState = StepState.Failed;
            BadgeText = "! 失败";
            StopEllipsis();
        }
    }

    private async Task ConfigureLocalAdapterAsync(CancellationToken ct)
    {
        SetStep(0, StepState.Active, "正在记录原始配置：" + _selectedAdapter!.Name);
        SetBusy("正在配置本机网卡...", "先记录原始配置，再将网卡切换到 " + _subnetConfig.ServerDisplay + "。");
        StartEllipsis();

        _originalConfig = NetworkConfigManager.CaptureOriginalConfig(_selectedAdapter);
        LogInfo("Original config: dhcp=" + _originalConfig.DhcpEnabled +
            " addrs=" + _originalConfig.StaticAddresses.Count +
            " gw=" + _originalConfig.Gateways.Count +
            " dns=" + _originalConfig.DnsServers.Count);

        if (!_originalConfig.DhcpEnabled)
        {
            LogInfo("Original was static, forcing DHCP first");
            SetStep(0, StepState.Active, "检测到当前是静态 IP，先恢复为 DHCP 以清理残留配置。");
            await NetworkConfigManager.ForceDhcpBestEffortAsync(_selectedAdapter, _subnetConfig, ct, releaseToolLease: false);
            await Task.Delay(1200, ct);
            _originalConfig = AdapterOriginalConfig.CreateDhcp();
        }

        await NetworkConfigManager.SetStaticForToolAsync(_selectedAdapter, _subnetConfig, ct);
        LogInfo("Static IP set: " + _subnetConfig.ServerDisplay);
        _dhcpServer = new DhcpServer(_subnetConfig);
        _dhcpServer.Logger = msg => LogInfo("[DHCP] " + msg);
        _dhcpServer.Start();
        await CompleteStepAsync(0, "✅ 本机网卡配置完成：" + _subnetConfig.ServerDisplay, ct);
        StopEllipsis();
    }

    private async Task WaitForLinkAsync(CancellationToken ct)
    {
        LogInfo("Link wait started");
        SetStep(1, StepState.Active, "请用网线连接服务器的 IPMI 管理口，正在等待 Link UP。");
        SetBusy("请插入网线", "等待检测到网线连接，预计几秒内完成。");
        StartEllipsis();

        var warned = false;
        var startTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            if (await NetworkConfigManager.IsLinkUpAsync(_selectedAdapter!, ct))
            {
                LogInfo("Link detected");
                await CompleteStepAsync(1, "✅ 网线已连接，链路已 UP", ct);
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
        SetBusy("正在等待 IPMI 获取 IP...", "如果 3 分钟内没有响应，请检查网线是否插在 IPMI 管理口。");
        StartEllipsis();

        var tcs = new TaskCompletionSource<DhcpLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, DhcpLease lease) => tcs.TrySetResult(lease);

        if (_dhcpServer is not null)
            _dhcpServer.LeaseAssigned += Handler;
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

            var lease = await tcs.Task;
            LogInfo("DHCP lease acquired: IP=" + lease.IpAddress + " MAC=" + (lease.MacAddress.Length > 0 ? string.Join("-", lease.MacAddress.Select(b => b.ToString("X2"))) : "none"));
            await CompleteStepAsync(2, "✅ 已检测到 IPMI 设备，IP：" + lease.IpAddress, ct);
            StopEllipsis();
            return lease;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("3 分钟内未收到 IPMI 的 DHCP 请求，请检查网线是否插在 IPMI 管理口。");
        }
        finally
        {
            if (_dhcpServer is not null)
                _dhcpServer.LeaseAssigned -= Handler;
        }
    }

    private void OpenBrowser(string ipAddress)
    {
        try
        {
            OpenBrowserRequested?.Invoke(ipAddress);
            LogInfo("Browser opened for http://" + ipAddress);
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
        LogInfo("Cleanup started");

        try
        {
            _flowCts?.Cancel();
            await WaitForFlowToStopAsync();

            LogInfo("Cleanup: DHCP server disposing");

            SetStep(4, StepState.Active, "正在关闭 DHCP Server 并恢复网卡为 DHCP...");
            StatusText = "正在清理并退出...";
            DetailText = "请稍候，正在把网卡恢复为 DHCP。";
            ActivityText = GetActivityText(4, StepState.Active);
            BadgeState = StepState.Active;
            BadgeText = "处理中";
            StartEllipsis();

            _dhcpServer?.Stop();
            _dhcpServer?.Dispose();
            _dhcpServer = null;
            LogInfo("Cleanup: DHCP server disposed");

            if (_selectedAdapter is not null)
            {
                LogInfo("Cleanup: restoring DHCP for " + _selectedAdapter.Name);
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                await NetworkConfigManager.ForceDhcpBestEffortAsync(_selectedAdapter, _subnetConfig, cleanupCts.Token, releaseToolLease: true);
                LogInfo("Cleanup: DHCP restore done");
            }

            SetStep(4, StepState.Done, "✅ 网卡已恢复为 DHCP，DHCP Server 已关闭");
            StatusText = "✅ 清理完成";
            DetailText = "网卡已恢复为 DHCP，可以安全退出。";
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
            SetStep(4, StepState.Failed, "❌ 自动恢复 DHCP 时遇到问题：" + ex.Message);
            StatusText = "❌ 清理失败，未退出";
            DetailText = "网卡可能尚未恢复为 DHCP。请检查网络设置，或再次点击「完成 / 退出」重试。";
            ActivityText = "DHCP Server 已尝试关闭，但网卡恢复未确认完成。";
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
    //  Copy IP
    // ════════════════════════════════════════════════════════════════

    private void CopyIp()
    {
        if (string.IsNullOrEmpty(_discoveredIp))
            return;

        Clipboard.SetText(_discoveredIp);
        CopyButtonText = "已复制 ✓";

        _copyFeedbackTimer?.Stop();
        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            CopyButtonText = "复制 IP";
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
                0 => "本机网卡和 DHCP 服务已准备好。",
                1 => "已检测到网线连接，链路已 UP。",
                2 => "已获取 IPMI 设备地址。",
                3 => "BMC 管理页面已打开。",
                _ => "清理完成，可以安全退出。"
            };
        }

        if (state == StepState.Failed)
        {
            return "遇到问题，请按提示检查后重试。";
        }

        if (state == StepState.Pending)
        {
            return index switch
            {
                4 => "完成登录后点击右下角按钮退出并恢复 DHCP。",
                _ => "等待上一步完成。"
            };
        }

        return index switch
        {
            0 => "正在将网卡设置为静态 IP " + _subnetConfig.ServerDisplay + "，请稍候...",
            1 => "正在等待你插入连接 IPMI 管理口的网线...",
            2 => "正在等待 IPMI 设备通过 DHCP 获取地址...",
            3 => "正在打开默认浏览器访问 BMC 管理页面...",
            _ => "正在关闭 DHCP 服务并恢复网卡为 DHCP..."
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

    private static string GetVersionText()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(version))
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString();

        if (string.IsNullOrWhiteSpace(version))
            return "v0.0.0";

        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
            version = version[..plusIndex];

        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : "v" + version;
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
