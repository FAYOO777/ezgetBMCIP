$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$appStartup = Get-Content -LiteralPath (Join-Path $repoRoot 'App.xaml.cs') -Raw -Encoding UTF8
$appXaml = Get-Content -LiteralPath (Join-Path $repoRoot 'App.xaml') -Raw -Encoding UTF8
$mainWindow = Get-Content -LiteralPath (Join-Path $repoRoot 'MainWindow.xaml') -Raw -Encoding UTF8
$mainWindowCode = Get-Content -LiteralPath (Join-Path $repoRoot 'MainWindow.xaml.cs') -Raw -Encoding UTF8
$consentDialog = Get-Content -LiteralPath (Join-Path $repoRoot 'ConsentDialog.xaml') -Raw -Encoding UTF8

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Contains {
    param([string]$Source, [string]$Needle, [string]$Message)
    Assert-True -Condition $Source.Contains($Needle) -Message $Message
}

Assert-Contains $appStartup 'ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark' `
    'Application startup does not map the Windows light/dark setting to the application theme.'
Assert-Contains $appStartup 'ApplicationThemeManager.Apply(' `
    'Application startup does not apply the selected light/dark theme.'
Assert-Contains $appStartup 'updateAccent: false' `
    'Application theme setup can still replace product colors with the Windows accent.'
Assert-Contains $appStartup 'ApplicationAccentColorManager.Apply(' `
    'Application startup does not set the fixed product accent palette.'
Assert-Contains $mainWindowCode 'SystemThemeWatcher.Watch(this, WindowBackdropType.None, updateAccents: false);' `
    'Main window does not follow light/dark changes independently from accent changes.'

foreach ($xaml in @($mainWindow, $consentDialog)) {
    Assert-Contains $xaml 'WindowBackdropType="None"' 'A product window still uses the wallpaper-responsive Mica backdrop.'
    Assert-Contains $xaml 'Background="{DynamicResource ApplicationBackgroundBrush}"' 'A product window does not use the active light/dark surface.'
}

Assert-True (-not $appXaml.Contains('x:Key="AppWindowBackgroundBrush"')) `
    'A fixed light-only window background still overrides dark mode.'
Assert-True (-not $appXaml.Contains('x:Key="TextFillColorPrimaryBrush"')) `
    'A fixed light-only text palette still overrides WPF-UI dark-mode resources.'

Write-Output 'Theme policy contract tests passed.'
