#nullable disable
using System;
using System.Threading.Tasks;
using System.Windows.Input;

#if NETFRAMEWORK
namespace EzGetBmcIp.Legacy
#else
namespace EzGetBmcIp
#endif
{
    public enum FirewallRepairStage
    {
        None,
        CheckingRules,
        AwaitingConsent,
        RestoringNetwork,
        ApplyingRules,
        VerifyingRules,
        Completed,
        Failed
    }

    public sealed partial class MainViewModel
    {
        private bool _firewallRepairBusy;
        private FirewallRepairStage _firewallRepairStage;
        private string _firewallRepairFeedback = "";
        private ICommand _repairFirewallCommand;
        public bool IsFirewallRepairBusy => _firewallRepairBusy;
        public FirewallRepairStage CurrentFirewallRepairStage => _firewallRepairStage;
        public bool FirewallRepairEnabled => !_firewallRepairBusy && !FirewallRepairWorkflowRunning &&
            SessionState.Network != NetworkLifecycleState.Restoring && SessionState.Network != NetworkLifecycleState.RestoreFailed;
        public string FirewallRepairFeedback => _firewallRepairFeedback;
#if !NETFRAMEWORK
        public bool ShowAdapterFirewallNotice =>
            AppPhase == AppPhase.AdapterSelection && !string.IsNullOrWhiteSpace(_firewallRepairFeedback);
#endif

        // These display-only projections are shared by the modern and Legacy
        // selection pages.  The page visibility itself remains platform-specific
        // because Legacy does not have AppPhase.
        public bool ShowAdapterFirewallRetry =>
            ShowAdapterFirewallNotice && _currentFirewallRisk.HasValue && _currentFirewallRisk != FirewallRiskLevel.None;
        public string AdapterFirewallNoticeTitle =>
            _firewallRepairStage == FirewallRepairStage.Failed
                ? "防火墙处理未完成"
                : _currentFirewallRisk == FirewallRiskLevel.None
                    ? "防火墙许可已处理"
                    : "防火墙处理结果";
        public PresentationSeverity AdapterFirewallNoticeSeverity =>
            _firewallRepairStage == FirewallRepairStage.Failed
                ? PresentationSeverity.Error
                : _currentFirewallRisk == FirewallRiskLevel.None
                    ? PresentationSeverity.Success
                    : FirewallSeverity;
        private bool FirewallRepairWorkflowRunning => SessionState.Workflow == DiscoveryWorkflowState.WaitingForLink ||
            SessionState.Workflow == DiscoveryWorkflowState.ConfiguringNetwork || SessionState.Workflow == DiscoveryWorkflowState.WaitingForDhcp ||
            SessionState.Workflow == DiscoveryWorkflowState.ProbingEndpoint;
        public bool ShowFirewallRepair => _firewallRepairBusy || !string.IsNullOrEmpty(_firewallRepairFeedback) ||
            (SessionState.Network != NetworkLifecycleState.Restoring && SessionState.Network != NetworkLifecycleState.RestoreFailed &&
             !FirewallRepairWorkflowRunning && _currentFirewallRisk.HasValue && _currentFirewallRisk != FirewallRiskLevel.None &&
             (SessionState.Workflow == DiscoveryWorkflowState.Failed || SessionState.Workflow == DiscoveryWorkflowState.EndpointReachable ||
              SessionState.Workflow == DiscoveryWorkflowState.EndpointUnreachable));
        public ICommand RepairFirewallCommand => _repairFirewallCommand ?? (_repairFirewallCommand = CreateFirewallRepairCommand());
        private ICommand CreateFirewallRepairCommand()
        {
#if NETFRAMEWORK
            return new RelayCommand(async _ => await RepairFirewallAsync());
#else
            return new RelayCommand(_ => RepairFirewallAsync(), _ => FirewallRepairEnabled);
#endif
        }
        private void SetFirewallRepairFeedback(string value)
        {
            _firewallRepairFeedback = value;
            OnPropertyChanged(nameof(FirewallRepairFeedback));
            OnPropertyChanged(nameof(ShowFirewallRepair));
            OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
            OnPropertyChanged(nameof(ShowAdapterFirewallRetry));
            OnPropertyChanged(nameof(AdapterFirewallNoticeTitle));
            OnPropertyChanged(nameof(AdapterFirewallNoticeSeverity));
#if !NETFRAMEWORK
            OnPropertyChanged(nameof(ModernRuntime));
#else
            OnPropertyChanged(nameof(LegacyRuntime));
#endif
        }
        private void SetFirewallRepairProgress(FirewallRepairStage stage, string message)
        {
            _firewallRepairStage = stage;
            OnPropertyChanged(nameof(CurrentFirewallRepairStage));
            SetFirewallRepairFeedback(message);
        }
        private void SetFirewallRepairBusy(bool value)
        {
            _firewallRepairBusy = value;
            OnPropertyChanged(nameof(IsFirewallRepairBusy));
            OnPropertyChanged(nameof(FirewallRepairEnabled));
            OnPropertyChanged(nameof(IsExitActionEnabled));
            OnPropertyChanged(nameof(StartButtonEnabled));
            OnPropertyChanged(nameof(AdapterSelectionEnabled));
            OnPropertyChanged(nameof(ShowFirewallRepair));
            OnPropertyChanged(nameof(ShowAdapterFirewallNotice));
            OnPropertyChanged(nameof(ShowAdapterFirewallRetry));
            OnPropertyChanged(nameof(AdapterFirewallNoticeTitle));
            OnPropertyChanged(nameof(AdapterFirewallNoticeSeverity));
#if !NETFRAMEWORK
            OnPropertyChanged(nameof(ShowModernRuntimeHost));
            OnPropertyChanged(nameof(ShowLegacySessionPages));
            OnPropertyChanged(nameof(ModernRuntime));
#else
            OnPropertyChanged(nameof(LegacyRuntime));
#endif
        }
        private static void LogFirewallRepair(string message)
        {
#if NETFRAMEWORK
            Log(message);
#else
            LogInfo(message);
#endif
        }
        private async Task RepairFirewallAsync()
        {
            var adapter = _selectedAdapter ?? SelectedAdapterItem;
            if (adapter == null || _firewallRepairBusy || FirewallRepairWorkflowRunning ||
                SessionState.Network == NetworkLifecycleState.Restoring || SessionState.Network == NetworkLifecycleState.RestoreFailed) return;
#if NETFRAMEWORK
            if (_isCleaningUp || _isClosing) return;
#else
            if (_isCleanupRunning) return;
#endif
            SetFirewallRepairProgress(FirewallRepairStage.CheckingRules, "正在重新检查防火墙规则…");
            SetFirewallRepairBusy(true);
            var recovered = false;
#if !NETFRAMEWORK
            var completed = false;
#endif
            try
            {
                var assessment = await AssessFirewallAsync(adapter);
                SetCurrentFirewallAssessment(assessment);
                var plan = await Task.Run(() =>
                {
                    using (var policy = new NativeFirewallRepairPolicy())
                        return FirewallRepair.Prepare(policy, FirewallAssessmentService.GetCurrentExecutablePath(), adapter.Name, assessment.NetworkCategory);
                });
                SetFirewallRepairProgress(FirewallRepairStage.AwaitingConsent, "规则检查已完成，请在弹出的确认窗口中核对处理内容。");
                if (!RequestConsent(plan.Notice()))
                {
                    SetFirewallRepairFeedback("已取消，未修改防火墙或网卡。可处理 Windows 访问提示后再次检查。");
                    return;
                }
                LogFirewallRepair("Firewall repair consent accepted: " + plan.ExecutablePath + "; profile=" + plan.Profile);
                SetFirewallRepairProgress(FirewallRepairStage.RestoringNetwork, "正在停止 DHCP 并恢复本机网卡…");
#if NETFRAMEWORK
                _isCleaningUp = true;
                try { await DoCleanupAsync(); recovered = true; }
                catch (Exception ex) { RecordCleanupFailure(ex); throw; }
                finally { _isCleaningUp = false; }
#else
                recovered = await CleanupForFirewallRepairAsync();
#endif
                if (!recovered)
                {
                    SetFirewallRepairFeedback("网卡恢复尚未完成，未修改防火墙。请先重试恢复网卡。");
                    return;
                }
                SetFirewallRepairProgress(FirewallRepairStage.ApplyingRules, "本机网卡已恢复，正在处理防火墙规则…");
                await Task.Run(() =>
                {
                    using (var policy = new NativeFirewallRepairPolicy())
                        FirewallRepair.Apply(policy, plan, p => FirewallRepair.SaveBackup(p, LogFirewallRepair), LogFirewallRepair);
                });
                SetFirewallRepairProgress(FirewallRepairStage.VerifyingRules, "防火墙规则已处理，正在复查结果…");
                SetCurrentFirewallAssessment(await AssessFirewallAsync(adapter));
                SetHistoryRetrySuggestion(null);
                ShowSupportBundleAction = false;
                ResetSessionStateForNewFlow();
                SetNetworkState(NetworkLifecycleState.ConfigurationRestored);
                _dhcpServerError = null;
                _lastDhcpWaitTimedOut = false;
                _lastTimeoutFirewallRisk = null;
#if NETFRAMEWORK
                _isFlowStarted = false;
                _isCleanupDone = false;
                _adapterSelectionEnabled = true;
                _startButtonEnabled = true;
                OnPropertyChanged(nameof(AdapterSelectionEnabled));
                OnPropertyChanged(nameof(StartButtonEnabled));
                OnPropertyChanged(nameof(ShowAdapterSelectionContent));
                OnPropertyChanged(nameof(ShowLegacyRuntimeCard));
#else
                AdapterSelectionEnabled = true;
                StartButtonEnabled = true;
                IsCleanupDone = false;
#endif
                StatusText = "已处理防火墙并恢复网卡";
                DetailText = "已保留本次网段 " + _subnetConfig.ServerDisplay + "，请核对后手动点击开始。";
#if NETFRAMEWORK
                SetFirewallRepairProgress(FirewallRepairStage.Completed, "已为" + plan.Category + "配置当前程序的 UDP/67 接收许可，并复查规则。请手动开始；是否收到设备请求仍需下一轮验证。");
#else
                SetFirewallRepairProgress(FirewallRepairStage.Completed, "已配置当前程序的 UDP/67 接收许可，即将返回网卡选择页。");
                completed = true;
#endif
            }
            catch (Exception ex)
            {
                LogFirewallRepair("Firewall repair preparation failed: " + ex);
                SetFirewallRepairProgress(FirewallRepairStage.Failed, (recovered ? "网卡已恢复；" : "") + "防火墙处理未完成：" + ex.Message +
                    " 请导出支持包，或由管理员处理后再试。");
                ShowSupportBundleAction = true;
            }
            finally
            {
                SetFirewallRepairBusy(false);
#if !NETFRAMEWORK
                if (completed)
                    AppPhase = AppPhase.AdapterSelection;
#endif
            }
        }
    }
}
