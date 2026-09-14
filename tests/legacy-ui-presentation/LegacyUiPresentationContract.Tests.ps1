$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$xaml = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainWindow.xaml') -Raw -Encoding UTF8
$vm = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainViewModel.cs') -Raw -Encoding UTF8
$window = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainWindow.xaml.cs') -Raw -Encoding UTF8
$consent = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\ConsentDialog.xaml') -Raw -Encoding UTF8
$consentCode = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\ConsentDialog.xaml.cs') -Raw -Encoding UTF8
$project = Get-Content -LiteralPath (Join-Path $repoRoot 'ezgetBMCIP.Legacy\ezgetBMCIP.Legacy.csproj') -Raw -Encoding UTF8
$runtime = Get-Content -LiteralPath (Join-Path $repoRoot 'ModernRuntimePresentation.cs') -Raw -Encoding UTF8

function Assert-True { param([bool]$Condition, [string]$Message); if (-not $Condition) { throw $Message } }
function Assert-Contains { param([string]$Source, [string]$Needle, [string]$Message); Assert-True $Source.Contains($Needle) $Message }

Assert-Contains $project '<Compile Include="..\ModernRuntimePresentation.cs"' 'Legacy does not link the shared runtime presentation.'
Assert-Contains $vm 'IRuntimePresentationSource' 'Legacy ViewModel does not implement the shared runtime source.'
Assert-Contains $vm 'public ModernRuntimePresentation LegacyRuntime' 'Legacy has no shared runtime projection.'
Assert-Contains $xaml 'x:Name="LegacyRuntimeHost"' 'Legacy runtime host is missing.'
Assert-Contains $xaml 'ShowLegacyRuntimeHost' 'Legacy runtime host is not visibility-gated.'
Assert-Contains $xaml 'ItemsSource="{Binding LegacyRuntime.Stages}"' 'Legacy does not render the shared three-stage rail.'
Assert-Contains $xaml 'Text="{Binding LegacyRuntime.Title}"' 'Legacy runtime title does not use the shared projection.'
Assert-Contains $xaml 'Text="{Binding LegacyRuntime.Summary}"' 'Legacy runtime summary does not use the shared projection.'
Assert-Contains $xaml 'ItemsSource="{Binding LegacyRuntime.Facts}"' 'Legacy runtime facts binding is missing.'
Assert-Contains $xaml 'RuntimeActionToVis' 'Legacy runtime actions are not selected from the shared action model.'
Assert-Contains $xaml 'Text="{Binding BrowserStatusText}"' 'Browser request status is not shown separately.'
Assert-Contains $xaml 'Visibility="{Binding ShowBrowserStatus, Converter={StaticResource BoolToVis}}"' 'Legacy browser status visibility is not limited to an endpoint result.'
Assert-True (-not $xaml.Contains('查看详情')) 'Legacy runtime still renders a diagnostic details expander.'
Assert-True (-not $xaml.Contains('LegacyRuntime.HasDetails')) 'Legacy runtime still binds removed diagnostic details.'
Assert-Contains $xaml 'Click="CollectSupportBundle_Click"' 'Legacy has no support-bundle action.'
Assert-Contains $xaml 'Text="{Binding AdapterFirewallNoticeTitle}"' 'Legacy firewall feedback still uses a hard-coded title.'
Assert-Contains $xaml 'BorderBrush="{Binding AdapterFirewallNoticeSeverity, Converter={StaticResource SeverityToBrush}}"' 'Legacy firewall feedback does not use the dynamic severity border.'
Assert-Contains $xaml 'Visibility="{Binding ShowAdapterFirewallRetry, Converter={StaticResource BoolToVis}}"' 'Legacy firewall retry action is not gated by the remaining risk.'
Assert-Contains $xaml 'Visibility="{Binding ShowSupportBundleAction, Converter={StaticResource BoolToVis}}"' 'Legacy support-bundle action is not gated by the diagnostic state.'
Assert-True (-not $xaml.Contains('Text="防火墙需要留意"')) 'Legacy still labels completed firewall processing as a warning.'
Assert-Contains $xaml 'Text="{Binding NetworkStatusText}"' 'Legacy footer does not use shared network status.'
Assert-Contains $xaml 'Content="{Binding ExitButtonText}"' 'Legacy footer does not use shared exit text.'
Assert-Contains $xaml 'Grid.Row="2" Background="#FFFFFF"' 'Legacy footer is not fixed outside the scroll area.'
Assert-Contains $xaml 'x:Name="ContentScroller"' 'Legacy has no named scrollable content surface.'
Assert-Contains $window 'ContentScroller.ScrollToTop()' 'Legacy navigation does not reset scroll position.'

# All runtime facts remain presentation-only; XAML must not branch on business enums.
foreach ($forbidden in @('NetworkLifecycleState.', 'FailureKind.', 'DiscoveryWorkflowState.')) {
    Assert-True (-not $xaml.Contains($forbidden)) "Legacy XAML directly branches on $forbidden"
}

# The shared model must preserve the distinct result and recovery semantics.
foreach ($text in @('CandidateAddressSource.ExistingConfiguredAddress', 'CandidateAddressSource.DhcpAck', 'BrowserLaunchResult.RequestFailed', 'NetworkLifecycleState.RestoreFailed')) {
    Assert-Contains (Get-Content (Join-Path $repoRoot 'SessionPresentation.cs') -Raw -Encoding UTF8) $text "Shared presentation is missing $text."
}
Assert-Contains $runtime 'IRuntimePresentationSource' 'Runtime presentation is not framework-neutral.'
Assert-True (-not $runtime.Contains('Math.Clamp')) 'Shared runtime presentation uses an API unavailable on net46.'
Assert-True (-not $runtime.Contains('ArgumentNullException.ThrowIfNull')) 'Shared runtime presentation uses an API unavailable on net46.'
Assert-True (-not $runtime.Contains('namespace EzGetBmcIp;')) 'Shared runtime presentation uses a file-scoped namespace unavailable on C# 8.'

# Consent parity: concise first notice, structured network notice, safe cancel focus.
Assert-Contains $vm 'CreateModernUsageRisk()' 'Legacy still uses the old verbose usage notice.'
Assert-Contains $vm 'CreateModernNetworkChange(' 'Legacy does not use the structured network notice.'
foreach ($text in @('NetworkChangeSummaryPanel', '本次会发生什么', '退出后如何恢复', '查看防火墙详情', '查看恢复详情', 'NetworkChange.FirewallNotice.Title', 'NetworkChange.FirewallNotice.Description', 'NetworkChange.FirewallNotice.Details')) {
    Assert-Contains $consent $text "Legacy consent is missing $text."
}
Assert-True (-not $consent.Contains('开始前检查')) 'Legacy consent still renders the removed checklist heading.'
Assert-True (-not $consent.Contains('NetworkChange.Checks')) 'Legacy consent still binds the removed checklist collection.'
Assert-Contains $consentCode 'FocusSafeCancel' 'Legacy consent does not focus the safe cancel action.'
Assert-Contains $consent 'Background="#005A9E"' 'Legacy consent cancel action is not highlighted.'
Assert-Contains $consent 'MinWidth="128"' 'Legacy consent uses a cramped fixed-width button.'

# Preflight parity and cancellation boundary.
foreach ($text in @('IsStartPreflightBusy', 'ShowStartPreflightOverlay', 'StartPreflightStageText', 'ShowStartPreflightOverlayAfterDelayAsync', '正在读取所选网卡配置…', '正在检查防火墙规则…', '检查完成，正在显示风险告知…')) {
    Assert-Contains $vm $text "Legacy preflight is missing $text."
}
Assert-Contains $vm 'CreateModernNetworkChange(' 'Legacy preflight does not open the structured network notice.'
Assert-Contains $vm '_startPreflightCts?.Cancel()' 'Legacy close/cancel does not cancel preflight.'

# DHCP display remains monotonic and testable, while the old blue progress line
# is not reintroduced into the Legacy runtime surface.
foreach ($text in @('private void StartDhcpElapsedTimer()', 'new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }', 'internal static string FormatDhcpElapsed(TimeSpan elapsed)')) {
    Assert-Contains $vm $text "Legacy DHCP timer contract missing $text."
}
Assert-True (-not $xaml.Contains('TimeProgressValue')) 'Legacy reintroduced the removed DHCP progress line.'
Assert-True (-not $xaml.Contains('RequestObserved')) 'Legacy XAML claims unsupported DHCP evidence.'

Assert-Contains $project '<TargetFramework>net46</TargetFramework>' 'Legacy no longer targets .NET Framework 4.6.'
Assert-True (-not $project.Contains('WPF-UI')) 'Legacy introduced WPF-UI.'
Assert-True (-not $project.Contains('Mica')) 'Legacy introduced a Mica dependency.'
Assert-Contains $xaml 'Width="700" Height="540"' 'Legacy default window size changed.'
Assert-Contains $xaml 'MinWidth="660" MinHeight="500"' 'Legacy minimum window size changed.'

$legacyAssemblyPath = Join-Path $repoRoot 'ezgetBMCIP.Legacy\bin\Debug\net46\ezgetBMCIP-legacy.exe'
if (Test-Path -LiteralPath $legacyAssemblyPath) {
    $assembly = [System.Reflection.Assembly]::LoadFrom($legacyAssemblyPath)
    $type = $assembly.GetType('EzGetBmcIp.Legacy.MainViewModel', $true)
    $format = $type.GetMethod('FormatDhcpElapsed', [System.Reflection.BindingFlags]::Static -bor [System.Reflection.BindingFlags]::NonPublic)
    Assert-True ($null -ne $format) 'Legacy elapsed formatter was not found.'
    Assert-True ($format.Invoke($null, @([TimeSpan]::FromSeconds(42))) -eq '00:42') 'Legacy elapsed formatter regressed.'
}

Write-Output 'Legacy UI presentation contract tests passed.'
