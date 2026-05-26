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
    private WiredAdapter? _selectedAdapter;
    private AdapterOriginalConfig? _originalConfig;
    private DispatcherTimer? _ellipsisTimer;
    private int _ellipsisDots;

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
        new StepItem("5. 完成后退出", "清理退出", "关闭 DHCP 服务并还原网卡配置")
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
        StartCommand = new RelayCommand(async _ => await StartFlowAsync(), _ => StartButtonEnabled);
        ExitCommand = new RelayCommand(_ => RequestClose?.Invoke());
        CopyIpCommand = new RelayCommand(_ => CopyIp());
        GoNextCommand = new RelayCommand(_ => GoNext());
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var adapters = await Task.Run(NetworkConfigManager.GetWiredAdapters);
            if (adapters.Count == 0)
                throw new InvalidOperationException("未检测到可用网卡，请确认网卡驱动已安装。");

            foreach (var a in adapters)
                Adapters.Add(a);
            SelectedAdapterItem = Adapters[0];
        }
        catch (Exception ex)
        {
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

        _selectedAdapter = SelectedAdapterItem;
        AdapterSelectionEnabled = false;
        StartButtonEnabled = false;
        AppPhase = AppPhase.FlowRunning;
        DiscoveredIp = null;
        CopyButtonText = "复制 IP";
        AdapterCardLine1 = "✓ " + _selectedAdapter.DisplayName;
        AdapterCardLine2 = "";
        _flowCts = new CancellationTokenSource();

        try
        {
            await ConfigureLocalAdapterAsync(_flowCts.Token);
            await WaitForLinkAsync(_flowCts.Token);
            var lease = await WaitForDhcpLeaseAsync(_flowCts.Token);
            DiscoveredIp = lease.IpAddress.ToString();
            SetStep(3, StepState.Active, "正在打开默认浏览器访问 BMC 管理页面。");
            SetBusy("正在打开 BMC 管理页面...", "BMC 地址：http://" + lease.IpAddress);
            OpenBrowser(lease.IpAddress.ToString());
            await CompleteStepAsync(3, "✅ 已自动打开 BMC 管理页面", _flowCts.Token);

            SetStep(4, StepState.Pending, "可以登录 BMC 后点击「完成 / 退出」还原网卡。");
            StatusText = "✅ 已自动打开 BMC 管理页面";
            DetailText = "BMC 地址：http://" + lease.IpAddress;
            BadgeState = StepState.Done;
            BadgeText = "✓ 已完成";
            ActivityText = "BMC 管理页面已打开，完成后点击右下角退出。";
            StopEllipsis();
        }
        catch (OperationCanceledException)
        {
            StatusText = "正在退出...";
            DetailText = "正在还原网卡配置。";
            StopEllipsis();
        }
        catch (Exception ex)
        {
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

        if (!_originalConfig.DhcpEnabled)
        {
            SetStep(0, StepState.Active, "检测到当前是静态 IP，先还原为 DHCP 以清理残留配置。");
            await NetworkConfigManager.ForceDhcpBestEffortAsync(_selectedAdapter, _subnetConfig, ct);
            await Task.Delay(1200, ct);
            _originalConfig = AdapterOriginalConfig.CreateDhcp();
        }

        await NetworkConfigManager.SetStaticForToolAsync(_selectedAdapter, _subnetConfig, ct);
        _dhcpServer = new DhcpServer(_subnetConfig);
        _dhcpServer.Start();
        await CompleteStepAsync(0, "✅ 本机网卡配置完成：" + _subnetConfig.ServerDisplay, ct);
        StopEllipsis();
    }

    private async Task WaitForLinkAsync(CancellationToken ct)
    {
        SetStep(1, StepState.Active, "请用网线连接服务器的 IPMI 管理口，正在等待 Link UP。");
        SetBusy("请插入网线", "等待检测到网线连接，预计几秒内完成。");
        StartEllipsis();

        var warned = false;
        var startTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            if (await NetworkConfigManager.IsLinkUpAsync(_selectedAdapter!, ct))
            {
                await CompleteStepAsync(1, "✅ 网线已连接，链路已 UP", ct);
                StopEllipsis();
                return;
            }

            if (!warned && (DateTime.UtcNow - startTime).TotalSeconds > 60)
            {
                DetailText = "⚠ 已等待 60 秒仍未检测到网线连接，请确认网线已直连服务器 IPMI 管理口。";
                warned = true;
            }

            await Task.Delay(1500, ct);
        }
    }

    private async Task<DhcpLease> WaitForDhcpLeaseAsync(CancellationToken ct)
    {
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
        OpenBrowserRequested?.Invoke(ipAddress);
    }

    private async Task CompleteStepAsync(int index, string description, CancellationToken ct)
    {
        SetStep(index, StepState.Done, description);
        await Task.Delay(1500, ct);
    }

    public async Task CleanupAsync()
    {
        try
        {
            _flowCts?.Cancel();
            SetStep(4, StepState.Active, "正在关闭 DHCP Server 并还原网卡配置...");
            StatusText = "正在清理并退出...";
            DetailText = "请稍候，正在把网卡恢复为启动前的配置。";
            ActivityText = GetActivityText(4, StepState.Active);
            BadgeState = StepState.Active;
            BadgeText = "处理中";
            StartEllipsis();

            _dhcpServer?.Stop();
            _dhcpServer?.Dispose();
            _dhcpServer = null;

            if (_selectedAdapter is not null)
            {
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                await NetworkConfigManager.ForceDhcpBestEffortAsync(_selectedAdapter, _subnetConfig, cleanupCts.Token);
            }

            SetStep(4, StepState.Done, "✅ 网卡配置已还原，DHCP Server 已关闭");
            StatusText = "✅ 清理完成";
            DetailText = "网卡配置已还原，可以安全退出。";
            ActivityText = GetActivityText(4, StepState.Done);
            BadgeState = StepState.Done;
            BadgeText = "✓ 已完成";
            StopEllipsis();
            IsCleanupDone = true;
        }
        catch (Exception ex)
        {
            SetStep(4, StepState.Failed, "❌ 自动还原时遇到问题：" + ex.Message);
            StopEllipsis();
            IsCleanupDone = true;

            try
            {
                await File.AppendAllTextAsync(
                    Path.Combine(AppContext.BaseDirectory, "ezgetBMCIP.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " cleanup failed: " + ex + Environment.NewLine);
            }
            catch { }
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

    public void CancelFlow()
    {
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
                4 => "完成登录后点击右下角按钮退出并恢复网卡。",
                _ => "等待上一步完成。"
            };
        }

        return index switch
        {
            0 => "正在将网卡设置为静态 IP " + _subnetConfig.ServerDisplay + "，请稍候...",
            1 => "正在等待你插入连接 IPMI 管理口的网线...",
            2 => "正在等待 IPMI 设备通过 DHCP 获取地址...",
            3 => "正在打开默认浏览器访问 BMC 管理页面...",
            _ => "正在关闭 DHCP 服务并恢复网卡配置..."
        };
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
