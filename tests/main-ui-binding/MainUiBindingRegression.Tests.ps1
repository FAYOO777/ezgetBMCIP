$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$xamlPath = Join-Path $repoRoot 'MainWindow.xaml'
$viewModelPath = Join-Path $repoRoot 'MainViewModel.cs'
$xaml = Get-Content -LiteralPath $xamlPath -Raw -Encoding UTF8
$viewModel = Get-Content -LiteralPath $viewModelPath -Raw -Encoding UTF8

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

# Verify the XAML remains well-formed before checking the binding attributes.
try {
    [xml]$null = $xaml
}
catch {
    throw "MainWindow.xaml is not well-formed XML: $($_.Exception.Message)"
}

# These display projections intentionally have no setter. Run.Text can choose a
# write-back binding mode in WPF, so its binding must state Mode=OneWay.
$historyProperties = @(
    'HistoryAdapterName', 'HistoryLocalAddress', 'HistoryBmcAddress',
    'HistoryEndpointText', 'HistoryLastConfirmedText')

foreach ($property in $historyProperties) {
    Assert-True $viewModel.Contains("public string $property =>") `
        "History projection '$property' is no longer getter-only; do not add a setter to satisfy XAML."
    Assert-True $xaml.Contains(('Text="{Binding ' + $property + ', Mode=OneWay}"')) `
        "Getter-only history projection '$property' is not explicitly bound OneWay."
}

# All current Run.Text bindings in the formal window are display-only and must
# explicitly declare their direction. This catches another startup-binding failure
# if a future read-only projection is added without Mode=OneWay.
$runBindings = [regex]::Matches($xaml, '<Run\b[^>]*\bText="\{Binding[^}]*\}"[^>]*/?>')
foreach ($binding in $runBindings) {
    Assert-True $binding.Value.Contains('Mode=OneWay') `
        "Run.Text binding is missing explicit OneWay mode: $($binding.Value)"
}

Write-Output 'Main UI binding regression tests passed.'
