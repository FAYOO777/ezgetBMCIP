$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$xaml = Get-Content -LiteralPath (Join-Path $repoRoot 'MainWindow.xaml') -Raw -Encoding UTF8
$mainWindow = Get-Content -LiteralPath (Join-Path $repoRoot 'MainWindow.xaml.cs') -Raw -Encoding UTF8
$viewModel = Get-Content -LiteralPath (Join-Path $repoRoot 'MainViewModel.cs') -Raw -Encoding UTF8
$presentation = Get-Content -LiteralPath (Join-Path $repoRoot 'SessionPresentation.cs') -Raw -Encoding UTF8

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Contains {
    param([string]$Source, [string]$Needle, [string]$Message)
    Assert-True -Condition $Source.Contains($Needle) -Message $Message
}

foreach ($page in @(
    'RestoreFailed', 'EndpointReachable', 'EndpointUnreachable',
    'WaitingForLink', 'WaitingForDhcp', 'ProbingEndpoint',
    'DhcpTimedOut', 'Failure', 'Cancelled', 'Restoring')) {
    Assert-Contains $xaml "ConverterParameter=$page" "Main XAML does not select the $page page from CurrentSessionPage."
}

Assert-True (-not $xaml.Contains('ShowLegacyCandidateCard')) `
    'The retired legacy candidate-result card remains in Main XAML.'
Assert-True (-not $viewModel.Contains('ShowLegacyCandidateCard')) `
    'The retired legacy candidate-result visibility projection remains in MainViewModel.'
Assert-Contains $viewModel 'public bool ShowLegacyRuntimeProgress => false;' `
    'Legacy runtime progress is still rendered on the formal runtime surface.'
Assert-Contains $viewModel 'public bool ShowModernRuntimeHost =>' `
    'Formal runtime surface has no unified modern host projection.'
Assert-Contains $viewModel 'Cleanup completed and the user is returning to adapter selection' `
    'History retry does not document the reset of its completed timeout session.'
Assert-Contains $viewModel 'ResetSessionStateForNewFlow();' `
    'History retry can return to adapter selection while retaining a terminal timeout page.'
Assert-Contains $xaml 'x:Name="ContentScroller"' 'Main UI has no named scroll surface for page-reset handling.'
Assert-Contains $xaml 'ShowStartPreflightOverlay' 'Main UI has no preflight loading overlay.'
Assert-Contains $xaml 'ui:ProgressRing' 'Preflight loading overlay does not use the existing WPF-UI progress ring.'
Assert-Contains $xaml 'Text="{Binding StartPreflightStageText}"' 'Preflight overlay does not expose its current stage.'
Assert-Contains $xaml 'Text="{Binding StartPreflightDetailText}"' 'Preflight overlay does not state that the adapter is still unchanged.'
Assert-Contains $mainWindow 'or SessionPageKind.HistoryRetrySuggestion' 'History retry does not reset the content scroll position.'
Assert-Contains $mainWindow 'or SessionPageKind.Ready' 'Returning to adapter selection does not reset the content scroll position.'

Assert-Contains $xaml 'Command="{Binding ExitCommand}"' 'Restore page does not reuse the existing exit/retry command.'
Assert-Contains $xaml 'Click="CollectSupportBundle_Click"' 'Restore page has no support-bundle action.'
Assert-Contains $xaml 'Text="{Binding FailureTechnicalDetail}"' 'Restore details omit technical failure detail.'
Assert-Contains $xaml 'Command="{Binding OpenManagementPageCommand}"' 'Reachable page has no preferred-scheme primary action.'
Assert-Contains $xaml 'Text="{Binding BrowserStatusText}"' 'Reachable page does not show browser status.'
Assert-True ($xaml.IndexOf('Content="打开管理页面"', [StringComparison]::Ordinal) -lt $xaml.IndexOf('Text="{Binding BrowserStatusText}"', [StringComparison]::Ordinal)) `
    'Reachable-page browser status appears before its primary management action.'
Assert-Contains $xaml 'Text="{Binding CandidateSourceText}"' 'Result pages do not show candidate source.'
Assert-Contains $xaml 'Text="{Binding ModernNetworkStatusText}"' 'Fixed footer does not bind the modern network-status projection.'
Assert-Contains $xaml 'Content="{Binding ExitButtonText}"' 'Fixed footer does not bind ExitButtonText.'
Assert-Contains $xaml 'IsEnabled="{Binding IsExitActionEnabled}"' 'Exit action does not use shared enabled semantics.'
Assert-True (-not $xaml.Contains('NetworkLifecycleState.')) 'Main XAML directly branches on network lifecycle state.'
Assert-Contains $xaml 'Text="{Binding DhcpElapsedText}"' 'DHCP waiting page does not show elapsed time.'
Assert-Contains $xaml 'Text="{Binding DhcpMaximumWaitText}"' 'DHCP waiting page does not show the fixed maximum wait.'
Assert-Contains $xaml 'Text="{Binding DhcpEvidenceText}"' 'DHCP waiting page does not use shared evidence copy.'
Assert-Contains $xaml 'Text="{Binding FailureRecommendedActionText}"' 'Failure page does not show FailureKind-derived guidance.'
Assert-Contains $xaml 'Text="{Binding FailureNetworkImpactText}"' 'Failure page does not show network-lifecycle impact.'
Assert-Contains $xaml 'Visibility="{Binding CanRetryEndpointFromFailure, Converter={StaticResource BoolToVis}}"' 'Failure retry endpoint action is not gated by candidate availability.'
Assert-Contains $xaml 'Text="这不是当前连接设备身份的证明，也不表示该历史地址仍然可用。"' 'History suggestion lacks its identity limitation.'
Assert-True (-not $xaml.Contains('没有收到 DHCP 请求')) 'Main XAML claims RequestObserved evidence that current DHCP code does not have.'

# Run.Text has a TwoWay-by-default metadata path in this WPF surface. The history
# values are getter-only display projections, so every such binding must state its
# one-way intent explicitly; otherwise MainWindow can fail during binding attach.
foreach ($property in @(
    'HistoryAdapterName', 'HistoryLocalAddress', 'HistoryBmcAddress',
    'HistoryEndpointText', 'HistoryLastConfirmedText')) {
    Assert-Contains $xaml ('Text="{Binding ' + $property + ', Mode=OneWay}"') `
        "History display binding '$property' is not explicitly OneWay."
}

foreach ($needle in @(
    'public bool ShowRuntimeHero =>',
    'public bool ShowLegacyRuntimeProgress =>',
    'public bool ShowHistoryRetryCard => CurrentSessionPage == SessionPageKind.HistoryRetrySuggestion;',
    'public bool ShowLegacySupportBundleCard =>',
    'public string DhcpElapsedText',
    'public bool ShowLongDhcpWaitHint =>',
    'public string DhcpEvidenceText =>',
    'public string FailureRecommendedActionText =>',
    'public bool CanRetryEndpointFromFailure =>',
    'private void StartDhcpElapsedTimer()',
    'private void StopDhcpElapsedTimer(bool reset)',
    'public ICommand OpenManagementPageCommand { get; }',
    'SessionState.IsDiscoverySuccessful && IsPreferredManagementScheme',
    'public string BrowserStatusText =>',
    'public string CandidateSourceText =>',
    'public string NetworkStatusText =>',
    'public string ModernNetworkStatusText =>',
    'public string RecoveryOriginalConfigDetails')) {
    Assert-Contains $viewModel $needle "Main ViewModel is missing '$needle'."
}

Assert-Contains $viewModel 'ConsentNotice.CreateModernUsageRisk()' 'Formal client does not use its concise usage-risk notice.'
Assert-Contains $viewModel 'ConsentNotice.CreateModernNetworkChange(' 'Formal client does not use the structured network-change notice.'
Assert-Contains $viewModel 'private CancellationTokenSource? _startPreflightCts;' 'Start preflight does not have an isolated cancellation source.'
Assert-Contains $viewModel 'private async Task ShowStartPreflightOverlayAfterDelayAsync(' 'Start preflight overlay is not delayed for fast checks.'
Assert-Contains $viewModel 'SetStartPreflightStage("正在检查防火墙规则…")' 'Start preflight does not expose the firewall-check stage.'
Assert-Contains $viewModel 'NetworkConfigManager.CaptureOriginalConfig(preflightAdapter)' 'Start preflight does not read the original adapter configuration.'
Assert-Contains $viewModel 'CaptureOriginalConfigForTests' 'Start preflight has no internal UI-test seam for slow or failed configuration reads.'
Assert-Contains $viewModel 'AssessFirewallForTestsAsync' 'Start preflight has no internal UI-test seam for slow or failed firewall checks.'

$consentNotice = Get-Content -LiteralPath (Join-Path $repoRoot 'ConsentNotice.cs') -Raw -Encoding UTF8
$consentDialog = Get-Content -LiteralPath (Join-Path $repoRoot 'ConsentDialog.xaml') -Raw -Encoding UTF8
$consentDialogCode = Get-Content -LiteralPath (Join-Path $repoRoot 'ConsentDialog.xaml.cs') -Raw -Encoding UTF8
Assert-Contains $consentNotice 'ConsentLayoutKind.NetworkChangeSummary' 'Consent model has no structured network-change layout marker.'
Assert-Contains $consentNotice 'public ModernNetworkChangeContent? NetworkChange' 'Consent model does not expose structured network-change content.'
Assert-Contains $consentNotice 'BuildFirewallNotice' 'Structured network-change consent has no conditional firewall notice mapping.'
Assert-Contains $consentNotice 'HasFirewallNotice' 'Structured network-change consent has no conditional firewall visibility.'
Assert-True (-not $consentNotice.Contains('NetworkChange.Checks')) 'Consent model still exposes the removed checklist collection.'
Assert-Contains $consentDialog 'x:Name="NetworkChangeSummaryPanel"' 'Consent dialog has no structured network-change summary panel.'
Assert-Contains $consentDialog 'x:Name="NetworkActionFooter"' 'Network-change consent has no fixed action footer.'
Assert-Contains $consentDialog 'x:Name="NetworkAcknowledgementCheckBox"' 'Network-change consent has no fixed acknowledgement control.'
Assert-Contains $consentDialog 'x:Name="NetworkCancelButton"' 'Network-change consent has no safe cancel action.'
Assert-Contains $consentDialog 'x:Name="NetworkCancelButton"' + [Environment]::NewLine + '                                   Content="取消 / 不同意"' + [Environment]::NewLine + '                                   Style="{StaticResource BrandPrimaryButtonStyle}"' 'Network-change consent does not highlight the safe cancel action.'
Assert-Contains $consentDialog 'x:Name="NetworkAgreeButton"' + [Environment]::NewLine + '                                   Content="{Binding ConfirmButtonText}"' + [Environment]::NewLine + '                                   Appearance="Secondary"' 'Network-change consent confirm action should remain secondary.'
Assert-True (-not $consentDialog.Contains('开始前检查')) 'Consent dialog still renders the removed checklist heading.'
Assert-True (-not $consentDialog.Contains('NetworkChange.Checks')) 'Consent dialog still binds the removed checklist collection.'
Assert-Contains $consentDialog 'x:Name="FirewallNoticeBorder"' 'Consent dialog has no conditional firewall notice.'
Assert-Contains $consentDialog 'NetworkChange.FirewallNotice.Title' 'Consent dialog does not bind the firewall notice title.'
Assert-Contains $consentDialog 'NetworkChange.FirewallNotice.Description' 'Consent dialog does not bind the firewall notice description.'
Assert-Contains $consentDialog 'NetworkChange.FirewallNotice.Details' 'Consent dialog does not bind the firewall notice details.'
Assert-Contains $consentDialog 'x:Name="GenericActionPanel"' 'Generic consent actions are not isolated from the network-change footer.'
Assert-Contains $consentDialog 'Grid.Row="1"' 'Network-change footer is not placed in a dedicated layout row.'
Assert-True (-not $consentDialog.Contains('NetworkFooterSpacer')) 'The old overlapping footer spacer remains in the consent dialog.'
Assert-Contains $consentDialog 'SolidBackgroundFillColorBaseBrush' 'Network-change footer does not use an opaque theme surface.'
Assert-Contains $consentDialog '查看防火墙详情' 'Structured network-change consent has no firewall details expander.'
Assert-Contains $consentDialog '查看恢复详情' 'Structured network-change consent has no restore details expander.'
Assert-Contains $consentDialog 'ConsentAttentionBrush' 'Consent dialog has no attention status color.'
Assert-Contains $consentDialog 'ConsentImpactBrush' 'Consent dialog has no impact status color.'
Assert-Contains $consentDialogCode 'ApplyNoticeLayout' 'Consent dialog does not apply the structured layout deterministically.'
Assert-Contains $consentDialogCode 'NetworkAcknowledgementCheckBox' 'Consent dialog does not handle the network acknowledgement independently.'
Assert-True (-not $consentDialogCode.Contains('NetworkFooterSpacer')) 'Consent dialog still controls the removed overlapping footer spacer.'

$mainWindowXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'MainWindow.xaml') -Raw -Encoding UTF8
Assert-Contains $mainWindowXaml 'x:Name="SupportProgressCard"' 'Main window has no support-progress card to verify.'
Assert-Contains $mainWindowXaml 'x:Name="StartPreflightOverlay"' 'Main window has no named preflight overlay.'
Assert-Contains $mainWindowXaml 'x:Name="StartPreflightCard"' 'Preflight overlay has no named central card.'
Assert-Contains $mainWindowXaml 'x:Key="PreflightScrimBrush"' 'Preflight overlay has no independent scrim brush.'
Assert-Contains $mainWindowXaml 'Background="{DynamicResource SolidBackgroundFillColorBaseBrush}"' 'Overlay cards do not use the opaque theme surface.'
Assert-True (-not $mainWindowXaml.Contains('Opacity="0.96"')) 'Preflight overlay still applies opacity to its entire content subtree.'

Assert-Contains $presentation 'case NetworkLifecycleState.RestoreFailed' 'Shared presentation no longer models RestoreFailed.'
Assert-Contains $presentation 'case BrowserLaunchResult.RequestFailed' 'Shared presentation no longer models browser request failure.'
Assert-Contains $presentation '地址可达结论不受影响' 'Browser failure copy incorrectly affects discovery success.'
Assert-Contains $presentation 'GetDhcpEvidencePresentation' 'Shared presentation lacks DHCP evidence wording.'
Assert-Contains $presentation 'GetFailureRecommendedActionText' 'Shared presentation lacks FailureKind guidance.'
Assert-Contains $presentation '当前防火墙配置存在兼容性风险，但尚不能确认它导致了本次等待。' 'Firewall Warning copy asserts or omits evidence strength.'
Assert-Contains $presentation '候选管理地址尚未确认可达' 'EndpointUnreachable headline does not preserve the reachability conclusion.'

Write-Output 'Main UI presentation contract tests passed.'
