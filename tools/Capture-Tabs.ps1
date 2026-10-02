<#
.SYNOPSIS
    Captures every tab of the main window (Session, Settings, Renumber) in the light and/or dark theme, at the default or a given size,
    for reviewing layout changes side by side.

.DESCRIPTION
    Dependencies: PowerShell 7+ on Windows (built-in UI Automation), tools/Capture-Window.ps1. No modules to install.
    Run from the repo root, with Screenshot Helper closed:
        pwsh tools/Capture-Tabs.ps1 -Exe 'src/ScreenshotHelper.App/bin/Release/net10.0-windows/Screenshot Helper.exe' -OutDir out/tabs
        pwsh tools/Capture-Tabs.ps1 -Exe '…\Screenshot Helper.exe' -OutDir out/tabs -Recents 10 -Theme Dark -Width 900 -Height 700

    DISRUPTIVE (mildly): the app's window opens on your desktop and its tabs are clicked through UI Automation. No keys are sent.

    Uses a scratch SCREENSHOTHELPER_HOME (your real settings aren't touched) with -Recents demo recent folders; the first holds a few
    demo shots so the Renumber tab has a list. Without -Width/-Height the window opens at its default size. Writes
    <tab>-<theme>.png to -OutDir. The demo folders live in your temp folder, so their paths (including your user name) appear in the
    images: these are for review, not for publishing. README images come from tools/Update-ReadmeScreenshots.ps1, which uses a neutral
    drive letter.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir,
    [ValidateRange(1, 10)] [int] $Recents = 3,
    [ValidateSet('Light', 'Dark', 'Both')] [string] $Theme = 'Both',
    [int] $Width,
    [int] $Height
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$exePath = (Resolve-Path -LiteralPath $Exe).Path
# Any running instance (whatever its exe is called) holds this object; a new launch would only activate it and exit.
if ([System.Threading.Mutex]::TryOpenExisting('Local\ScreenshotHelper.SingleInstance', [ref]$null)) { throw 'Close Screenshot Helper first (tray icon → Exit).' }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$themes = if ($Theme -eq 'Both') { 'Light', 'Dark' } else { $Theme }
$previousHome = $env:SCREENSHOTHELPER_HOME

foreach ($variant in $themes) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "sshelper-tabs-$([Guid]::NewGuid().ToString('N'))"
    $folders = @(1..$Recents | ForEach-Object { (New-Item -ItemType Directory -Force -Path (Join-Path $scratch "Project screenshots $_")).FullName })
    foreach ($g in 1..6) { foreach ($s in 1..4) { Copy-Item (Join-Path $root 'src/ScreenshotHelper.App/Assets/app-256.png') (Join-Path $folders[0] "$g-$s.png") } }
    Set-Content (Join-Path $scratch 'settings.json') (@{ theme = $variant } | ConvertTo-Json)
    $state = @{ recentFolders = $folders }
    if ($Width -gt 0 -and $Height -gt 0) { $state.windowWidth = $Width; $state.windowHeight = $Height }
    Set-Content (Join-Path $scratch 'state.json') ($state | ConvertTo-Json)

    $process = $null
    try {
        $env:SCREENSHOTHELPER_HOME = $scratch
        $process = Start-Process $exePath -PassThru
        do { Start-Sleep -Milliseconds 200; $process.Refresh() } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.HasExited)
        if ($process.HasExited) { throw 'The app exited during start-up.' }
        Start-Sleep -Milliseconds 1200
        $window = $A::FromHandle($process.MainWindowHandle)
        $capture = Join-Path $PSScriptRoot 'Capture-Window.ps1'

        $tabs = [ordered]@{ session = $null; settings = 'Settings'; renumber = 'Renumber existing screenshots' }
        foreach ($tab in $tabs.GetEnumerator()) {
            if ($tab.Value) {
                $window.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $tab.Value))).
                    GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Start-Sleep -Milliseconds 900
            }
            $output = Join-Path $OutDir "$($tab.Key)-$($variant.ToLowerInvariant()).png"
            & pwsh -NoProfile -File $capture -ProcessName $process.ProcessName -Output $output | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Capturing $output failed." }
            Write-Host "Captured $output"
        }
    }
    finally {
        if ($process -and -not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
        }
        $env:SCREENSHOTHELPER_HOME = $previousHome
        Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
    }
}
