$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$mainXaml = Get-Content (Join-Path $repoRoot 'MainWindow.xaml') -Raw -Encoding UTF8
$legacyXaml = Get-Content (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainWindow.xaml') -Raw -Encoding UTF8
$mainVm = Get-Content (Join-Path $repoRoot 'MainViewModel.cs') -Raw -Encoding UTF8
$legacyVm = Get-Content (Join-Path $repoRoot 'ezgetBMCIP.Legacy\MainViewModel.cs') -Raw -Encoding UTF8
$presentation = Get-Content (Join-Path $repoRoot 'SessionPresentation.cs') -Raw -Encoding UTF8
$runtime = Get-Content (Join-Path $repoRoot 'ModernRuntimePresentation.cs') -Raw -Encoding UTF8

function Assert-True { param([bool]$Condition, [string]$Message); if (-not $Condition) { throw $Message } }
function Assert-Contains { param([string]$Source, [string]$Needle, [string]$Message); Assert-True $Source.Contains($Needle) $Message }

foreach ($source in @($mainVm, $legacyVm)) {
    Assert-Contains $source 'SessionPresentation.GetSessionPagePresentation(' 'A ViewModel bypasses the shared page projection.'
    Assert-Contains $source 'SessionPresentation.GetNetworkStatusPresentation(' 'A ViewModel bypasses the shared network-status projection.'
    Assert-Contains $source 'SessionPresentation.GetExitActionText(SessionState.ExitIntent)' 'A ViewModel maintains independent exit text.'
    Assert-Contains $source 'SessionPresentation.IsExitActionEnabled(SessionState.ExitIntent)' 'A ViewModel maintains independent exit semantics.'
    Assert-Contains $source 'IRuntimePresentationSource' 'A ViewModel does not use the shared runtime source.'
}

Assert-Contains $mainVm 'ModernRuntimePresentation.From(this)' 'Modern host does not use the shared runtime projection.'
Assert-Contains $legacyVm 'ModernRuntimePresentation.From(this)' 'Legacy host does not use the shared runtime projection.'
Assert-Contains $legacyXaml 'ItemsSource="{Binding LegacyRuntime.Stages}"' 'Legacy does not render shared stages.'
Assert-Contains $mainXaml 'ItemsSource="{Binding ModernRuntime.Stages}"' 'Modern does not render shared stages.'

foreach ($xaml in @($mainXaml, $legacyXaml)) {
    foreach ($binding in @('Command="{Binding ExitCommand}"', 'IsEnabled="{Binding IsExitActionEnabled}"')) {
        Assert-Contains $xaml $binding 'A UI footer does not use the shared exit projection.'
    }
    Assert-Contains $xaml 'Text="{Binding BrowserStatusText}"' 'Browser request status is not shown separately from discovery.'
    Assert-Contains $xaml 'Content="使用上次网段重试"' 'History retry primary action text differs from the shared contract.'
    Assert-Contains $xaml 'Content="尝试 HTTPS"' 'An HTTP-only result has no HTTPS fallback action.'
    Assert-Contains $xaml 'ShowHttpsFallbackAction' 'The HTTPS fallback action is not gated by shared endpoint evidence.'
    Assert-Contains $xaml 'Content="改用 HTTP"' 'A dual-port result has no HTTP alternate action.'
    Assert-Contains $xaml 'ShowHttpFallbackAction' 'The HTTP alternate action is not gated by shared endpoint evidence.'
    Assert-True (-not $xaml.Contains('没有收到 DHCP 请求')) 'A UI claims unsupported DHCP request-observation evidence.'
}

foreach ($obsoleteCopy in @('BMC 页面已打开', 'BMC 地址已分配', '已分配 BMC 管理地址', '页面验证成功', '完成 / 退出', 'BMC IP 获取成功', '防火墙导致', '未收到 DHCP 请求')) {
    foreach ($source in @($mainXaml, $legacyXaml, $mainVm, $legacyVm, $presentation)) {
        Assert-True (-not $source.Contains($obsoleteCopy)) "Current UI projection retains obsolete copy: $obsoleteCopy"
    }
}

Assert-Contains $presentation 'if (sessionState.Network == NetworkLifecycleState.RestoreFailed)' 'RestoreFailed is not a page-selection priority.'
Assert-Contains $presentation 'return SessionPageKind.RestoreFailed;' 'RestoreFailed does not select its own page.'
foreach ($property in @('HistoryAdapterName', 'HistoryLocalAddress', 'HistoryBmcAddress', 'HistoryEndpointText', 'HistoryLastConfirmedText')) {
    Assert-Contains $mainVm "public string $property" "Main ViewModel omits history fact $property."
    Assert-Contains $legacyVm "public string $property" "Legacy ViewModel omits history fact $property."
}

foreach ($copy in @('显式防火墙阻止规则', '尚不能确认它导致了本次等待', '未发现明显的防火墙阻断证据', '无法完整评估当前防火墙配置')) {
    Assert-Contains $presentation $copy 'Shared evidence wording regressed.'
}
Assert-Contains $presentation '未能打开系统浏览器。' 'Shared browser status wording is missing.'
Assert-True (-not $presentation.Contains('地址可达结论不受影响')) 'Shared presentation retains obsolete browser wording.'
Assert-Contains $runtime 'public interface IRuntimePresentationSource' 'Runtime projection is not shared.'
Assert-Contains $runtime 'public bool ShowHttpsFallbackAction' 'Shared runtime projection omits the HTTPS fallback state.'
Assert-Contains $runtime 'public bool ShowHttpFallbackAction' 'Shared runtime projection omits the HTTP alternate state.'
Assert-True (-not $runtime.Contains('namespace EzGetBmcIp;')) 'Shared runtime projection is not C# 8 compatible.'

Write-Output 'Main/Legacy UI parity contract tests passed.'
