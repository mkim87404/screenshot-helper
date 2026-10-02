<#
.SYNOPSIS
    Measures how long Screenshot Helper takes to start: time to its first visible window, and time until the Session tab is usable.

.DESCRIPTION
    Dependencies: PowerShell 7+ on Windows (uses the built-in UI Automation assemblies). No modules to install.
    Run from the repo root, with Screenshot Helper closed, against any built or published exe:
        pwsh tools/Measure-Startup.ps1 -Exe 'src/ScreenshotHelper.App/bin/Release/net10.0-windows/Screenshot Helper.exe'
        pwsh tools/Measure-Startup.ps1 -Exe artifacts/ScreenshotHelper-1.0.0-win-x64.exe -Runs 7

    DISRUPTIVE (mildly): the app's window opens and closes on your desktop once per run. No keys are sent.

    Each run launches the exe against a fresh scratch SCREENSHOTHELPER_HOME (your real settings and logs aren't touched), records
      - Window: process start → its first visible top-level window (what the user first sees), and
      - Ready:  process start → the "Start session" button is reachable through UI Automation (layout done, view usable),
    then closes the app through its window (the normal exit path) and waits for the process to end. The first run is reported
    separately as "cold" (file cache, single-file extraction); the median and minimum of the rest are the warm figures.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Exe,
    [ValidateRange(2, 50)] [int] $Runs = 6
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$exePath = (Resolve-Path -LiteralPath $Exe).Path
$processName = [IO.Path]::GetFileNameWithoutExtension($exePath)
# Any running instance (whatever its exe is called) holds this object; a new launch would only activate it and exit.
if ([System.Threading.Mutex]::TryOpenExisting('Local\ScreenshotHelper.SingleInstance', [ref]$null)) { throw 'Close Screenshot Helper first (tray icon → Exit): a running instance would just be activated.' }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Namespace StartupProbe -Name Native -MemberDefinition @'
    public delegate bool EnumProc(System.IntPtr hwnd, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int attr, out int value, int size);

    // A window counts as visible when it has WS_VISIBLE and DWM isn't cloaking it (DWMWA_CLOAKED = 14).
    public static bool HasVisibleWindow(uint pid)
    {
        bool found = false;
        EnumWindows((h, l) =>
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner == pid && IsWindowVisible(h))
            {
                int cloaked;
                if (DwmGetWindowAttribute(h, 14, out cloaked, 4) != 0 || cloaked == 0) { found = true; return false; }
            }
            return true;
        }, System.IntPtr.Zero);
        return found;
    }
'@

$A = [System.Windows.Automation.AutomationElement]
$condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Start session')),
    (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))

$previousHome = $env:SCREENSHOTHELPER_HOME
$results = [System.Collections.Generic.List[object]]::new()
try {
    for ($i = 1; $i -le $Runs; $i++) {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) "sshelper-startup-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $scratch | Out-Null
        $env:SCREENSHOTHELPER_HOME = $scratch
        $process = $null
        try {
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $process = Start-Process -FilePath $exePath -PassThru
            $window = $null
            while ($null -eq $window) {
                if ($process.HasExited) { throw "The app exited during start-up (run $i)." }
                if ($clock.Elapsed.TotalSeconds -gt 30) { throw "No window within 30 s (run $i)." }
                if ([StartupProbe.Native]::HasVisibleWindow([uint32]$process.Id)) { $window = $clock.Elapsed.TotalMilliseconds } else { Start-Sleep -Milliseconds 5 }
            }

            $root = $null
            $ready = $null
            while ($null -eq $ready) {
                if ($clock.Elapsed.TotalSeconds -gt 30) { throw "The Session tab wasn't ready within 30 s (run $i)." }
                $process.Refresh()
                if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                    $root ??= $A::FromHandle($process.MainWindowHandle)
                    if ($root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)) { $ready = $clock.Elapsed.TotalMilliseconds; break }
                }
                Start-Sleep -Milliseconds 10
            }

            $results.Add([pscustomobject]@{ Run = $i; WindowMs = [math]::Round($window); ReadyMs = [math]::Round($ready) })
            Write-Host ("run {0}: window {1:N0} ms, ready {2:N0} ms" -f $i, $window, $ready)
        }
        finally {
            if ($process -and -not $process.HasExited) {
                $null = $process.CloseMainWindow()
                if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force; $null = $process.WaitForExit(5000) }
            }
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 500
    }
}
finally {
    $env:SCREENSHOTHELPER_HOME = $previousHome
}

function Get-Median([double[]] $values) {
    $sorted = $values | Sort-Object
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { $sorted[$mid] } else { ($sorted[$mid - 1] + $sorted[$mid]) / 2 }
}

$warm = $results | Select-Object -Skip 1
[pscustomobject]@{
    Exe = Split-Path -Leaf $exePath
    ColdWindowMs = $results[0].WindowMs
    ColdReadyMs = $results[0].ReadyMs
    WarmWindowMedianMs = Get-Median $warm.WindowMs
    WarmReadyMedianMs = Get-Median $warm.ReadyMs
    WarmReadyMinMs = ($warm.ReadyMs | Measure-Object -Minimum).Minimum
} | Format-List
