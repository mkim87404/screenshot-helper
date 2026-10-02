<#
.SYNOPSIS
    Times switching between the main window's tabs (Session, Renumber, Settings) with a large demo folder and many recent folders.

.DESCRIPTION
    Dependencies: PowerShell 7+ on Windows (built-in UI Automation assemblies). No modules to install.
    Run from the repo root, with Screenshot Helper closed:
        pwsh tools/Measure-TabSwitch.ps1 -Exe 'src/ScreenshotHelper.App/bin/Release/net10.0-windows/Screenshot Helper.exe'
        pwsh tools/Measure-TabSwitch.ps1 -Exe artifacts/ScreenshotHelper-1.0.0-win-x64.exe -Groups 40 -Subs 20 -Rounds 3

    DISRUPTIVE (mildly): the app's window opens on your desktop and its tabs are clicked through UI Automation. No keys are sent.

    Creates a scratch SCREENSHOTHELPER_HOME (your real settings aren't touched) whose Session folder holds Groups × Subs demo shots
    (default 40 × 20 = 800) plus 9 more empty recent folders, then for each round switches to Settings, Renumber and Session and times
    how long until the page is usable: a marker element appears (the Theme box; the first Renumber row; the Start session button).

    Finding an element through UI Automation itself takes time (it walks the window's tree), so a calibration step first times the
    marker lookup on a page that's already showing. Subtract it from the switch time to get the app's own share.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [ValidateRange(1, 100)] [int] $Groups = 40,
    [ValidateRange(1, 100)] [int] $Subs = 20,
    [ValidateRange(1, 10)] [int] $Rounds = 3
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
$scratch = Join-Path ([IO.Path]::GetTempPath()) "sshelper-tabs-$([Guid]::NewGuid().ToString('N'))"
$folder = Join-Path $scratch 'shots'
New-Item -ItemType Directory -Path $folder | Out-Null
$sample = Join-Path $root 'src/ScreenshotHelper.App/Assets/app-256.png'
foreach ($g in 1..$Groups) { foreach ($s in 1..$Subs) { Copy-Item $sample (Join-Path $folder "$g-$s.png") } }
$recents = @($folder) + @(1..9 | ForEach-Object { (New-Item -ItemType Directory -Path (Join-Path $scratch "recent $_")).FullName })
Set-Content (Join-Path $scratch 'settings.json') (@{ openFolderOnSessionEnd = $false } | ConvertTo-Json)
Set-Content (Join-Path $scratch 'state.json') (@{ recentFolders = $recents } | ConvertTo-Json)

$previousHome = $env:SCREENSHOTHELPER_HOME
$process = $null
try {
    $env:SCREENSHOTHELPER_HOME = $scratch
    $process = Start-Process $exePath -PassThru
    do { Start-Sleep -Milliseconds 200; $process.Refresh() } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.HasExited)
    if ($process.HasExited) { throw 'The app exited during start-up.' }
    Start-Sleep -Milliseconds 1500
    $window = $A::FromHandle($process.MainWindowHandle)

    function Find([string] $name) { $window.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $name))) }
    function Invoke-Tab([string] $name) { (Find $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

    # Tab button → marker element that means the page is usable.
    $tabs = [ordered]@{
        Settings = @('Settings', 'Theme')
        Renumber = @('Renumber existing screenshots', 'Preview 1-1.png')
        Session  = @('Session: choose a folder and numbering, then start capturing', 'Start session')
    }

    Write-Host 'Calibration (marker lookup on a page already showing; part of every switch time below):'
    foreach ($tab in $tabs.Keys) {
        $button, $marker = $tabs[$tab]
        Invoke-Tab $button
        Start-Sleep -Milliseconds 800
        $times = foreach ($i in 1..3) { $clock = [Diagnostics.Stopwatch]::StartNew(); $null = Find $marker; $clock.ElapsedMilliseconds }
        Write-Host ("  {0,-9} {1} ms" -f $tab, ($times -join ', '))
    }

    Write-Host "Switches ($($Groups * $Subs) shots in the Session folder, $($recents.Count) recent folders):"
    foreach ($round in 1..$Rounds) {
        foreach ($tab in $tabs.Keys) {
            $button, $marker = $tabs[$tab]
            $clock = [Diagnostics.Stopwatch]::StartNew()
            Invoke-Tab $button
            # Done when the marker is there (for Session, also once the Renumber list is gone, so the old page isn't mistaken for it).
            while (-not (Find $marker) -or ($tab -eq 'Session' -and (Find 'Preview 1-1.png'))) {
                if ($clock.Elapsed.TotalSeconds -gt 20) { throw "$tab never appeared." }
                Start-Sleep -Milliseconds 5
            }
            Write-Host ("  round {0}  {1,-9} {2} ms" -f $round, $tab, $clock.ElapsedMilliseconds)
            Start-Sleep -Milliseconds 500
        }
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
