<#
.SYNOPSIS
    Films the main window while its theme is switched light → dark and back, to check the switch for blank frames, flashes or jumps.

.DESCRIPTION
    Dependencies: PowerShell 7+ on Windows (built-in UI Automation), tools/Record-Frames.ps1. No modules to install.
    Run from the repo root, with Screenshot Helper closed:
        pwsh tools/Record-ThemeSwitch.ps1 -Exe 'src/ScreenshotHelper.App/bin/Release/net10.0-windows/Screenshot Helper.exe'
        pwsh tools/Record-ThemeSwitch.ps1 -Exe '…\Screenshot Helper.exe' -SaveFrames out/theme-frames

    DISRUPTIVE (mildly): the app's window opens on your desktop and its Settings tab is driven through UI Automation. No keys are sent.

    Launches the app in the dark theme against a scratch SCREENSHOTHELPER_HOME (your real settings aren't touched), opens Settings, then
    for each switch (to Light, then back to Dark) starts tools/Record-Frames.ps1 attached to the running app in a background job,
    changes the Theme box, and prints the recorder's per-frame timeline: the window rectangle and the brightness of its title bar, page
    and bottom. A clean switch changes the page in one step with no frame in between and never moves the window.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [string] $SaveFrames
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$exePath = (Resolve-Path -LiteralPath $Exe).Path
# Any running instance (whatever its exe is called) holds this object; a new launch would only activate it and exit.
if ([System.Threading.Mutex]::TryOpenExisting('Local\ScreenshotHelper.SingleInstance', [ref]$null)) { throw 'Close Screenshot Helper first (tray icon → Exit).' }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$recorder = Join-Path $PSScriptRoot 'Record-Frames.ps1'

$scratch = Join-Path ([IO.Path]::GetTempPath()) "sshelper-theme-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
Set-Content (Join-Path $scratch 'settings.json') (@{ theme = 'Dark' } | ConvertTo-Json)
$previousHome = $env:SCREENSHOTHELPER_HOME
$process = $null
try {
    $env:SCREENSHOTHELPER_HOME = $scratch
    $process = Start-Process $exePath -PassThru
    do { Start-Sleep -Milliseconds 200; $process.Refresh() } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.HasExited)
    if ($process.HasExited) { throw 'The app exited during start-up.' }
    Start-Sleep -Milliseconds 1200
    $window = $A::FromHandle($process.MainWindowHandle)
    $window.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Settings'))).
        GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800

    foreach ($theme in 'Light', 'Dark') {
        $frames = if ($SaveFrames) { Join-Path $SaveFrames "to-$($theme.ToLowerInvariant())" } else { $null }
        $job = Start-Job -ScriptBlock {
            param($recorder, $name, $frames)
            $arguments = @('-NoProfile', '-File', $recorder, '-AttachTo', $name, '-Seconds', '2.5')
            if ($frames) { $arguments += @('-SaveFrames', $frames) }
            & pwsh @arguments
        } -ArgumentList $recorder, $process.ProcessName, $frames
        Start-Sleep -Milliseconds 1200   # let the recorder start filming before the switch

        $box = $window.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Theme')),
            (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))))
        $box.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 300
        $box.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $theme))).
            GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()

        Write-Host "=== switched to $theme"
        Receive-Job $job -Wait -AutoRemoveJob | Out-String | Write-Host
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
