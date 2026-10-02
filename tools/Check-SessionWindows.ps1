<#
.SYNOPSIS
    Drives a real session and checks the windows that appear during it: the caption box (revealed already drawn, with keyboard focus)
    and the toast (on screen, click-through and uncovered after every shot, including with the taskbar focused; the "paused" toast
    staying up until the session resumes; and a second launch of the app showing "already running" before falling back to "paused").

.DESCRIPTION
    Dependencies: PowerShell 7+ on Windows (built-in UI Automation and Windows Forms assemblies). No modules to install.
    Run from the repo root, with Screenshot Helper closed:
        pwsh tools/Check-SessionWindows.ps1 -Exe 'src/ScreenshotHelper.App/bin/Release/net10.0-windows/Screenshot Helper.exe'

    DISRUPTIVE: it runs on your desktop for about 25 seconds: starts a session (registering the session keys), presses them with
    SendKeys, focuses the taskbar once with Win+T (opens nothing), takes three real screenshots of the monitor under the mouse into a scratch folder (deleted afterwards), and opens the caption
    box. Keep your hands off the keyboard and mouse. Caption text is typed only after the caption box is confirmed to be the foreground
    window, so a failure can't type into another app.

    The toast is excluded from screen capture, so it's checked through Windows instead of pixels: visible, not cloaked, entirely inside
    the working area of a screen, click-through, capture-excluded, and with no window above it overlapping it. Uses a scratch SCREENSHOTHELPER_HOME; your settings and logs aren't touched. Exits 1 on any
    failed check.
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Exe)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$exePath = (Resolve-Path -LiteralPath $Exe).Path
# Any running instance (whatever its exe is called) holds this object; a new launch would only activate it and exit.
if ([System.Threading.Mutex]::TryOpenExisting('Local\ScreenshotHelper.SingleInstance', [ref]$null)) { throw 'Close Screenshot Helper first (tray icon → Exit).' }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type -Namespace SessionCheck -Name Native -MemberDefinition @'
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(System.IntPtr hwnd, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, System.IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowText(System.IntPtr hwnd, System.Text.StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr hwnd, out RECT rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int attr, out int value, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern long GetWindowLongPtrW(System.IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(System.IntPtr hwnd, out uint affinity);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.IntPtr extra);

    // Visible, uncloaked top-level windows above the target in z-order that overlap it (e.g. a slid-out auto-hide taskbar).
    public static int CoveringWindows(System.IntPtr target)
    {
        RECT tr; GetWindowRect(target, out tr);
        int count = 0;
        EnumWindows((h, l) =>
        {
            if (h == target) return false;
            RECT r; GetWindowRect(h, out r); int cl;
            if (IsWindowVisible(h) && r.Left < tr.Right && r.Right > tr.Left && r.Top < tr.Bottom && r.Bottom > tr.Top
                && (DwmGetWindowAttribute(h, 14, out cl, 4) != 0 || cl == 0)) count++;
            return true;
        }, System.IntPtr.Zero);
        return count;
    }

    public static System.IntPtr Find(uint pid, string title)
    {
        System.IntPtr found = System.IntPtr.Zero;
        EnumWindows((h, l) =>
        {
            uint owner; GetWindowThreadProcessId(h, out owner);
            var text = new System.Text.StringBuilder(256); GetWindowText(h, text, 256);
            // Prefer a visible match: Avalonia can keep more than one native window with the same title.
            if (owner == pid && text.ToString() == title) { if (IsWindowVisible(h)) { found = h; return false; } if (found == System.IntPtr.Zero) found = h; }
            return true;
        }, System.IntPtr.Zero);
        return found;
    }

    // On screen = WS_VISIBLE and not cloaked by DWM (DWMWA_CLOAKED = 14).
    public static bool OnScreen(System.IntPtr hwnd)
    {
        int cloaked;
        return hwnd != System.IntPtr.Zero && IsWindowVisible(hwnd) && (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) != 0 || cloaked == 0);
    }
'@

$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$failures = [System.Collections.Generic.List[string]]::new()
$toastState = ''
function Check([bool] $ok, [string] $what) {
    $detail = if (-not $ok -and $what -like '*toast*') { " [$script:toastState]" } else { '' }
    Write-Host ("{0} {1}{2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $what, $detail)
    if (-not $ok) { $failures.Add($what) }
}

# Waits up to $ms for $condition, returning whether it became true.
function Wait-Until([scriptblock] $condition, [int] $ms = 3000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($ms)
    while ([DateTime]::UtcNow -lt $deadline) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 25 }
    return [bool](& $condition)
}

function Wait-Log([string] $pattern, [int] $seconds = 10) {
    $ok = Wait-Until { $logs = Get-ChildItem (Join-Path $scratch 'logs') -Filter 'app-*.log' -ErrorAction SilentlyContinue; $logs -and (Select-String -Path $logs.FullName -Pattern $pattern -Quiet) } ($seconds * 1000)
    if (-not $ok) { throw "Timed out waiting for the log line /$pattern/." }
}

# The toast must be visible, uncloaked, and inside one screen's working area. $script:toastState says why not, for the report.
function Test-ToastOnScreen {
    $toast = [SessionCheck.Native]::Find([uint32]$process.Id, 'Screenshot Helper notification')
    if ($toast -eq [IntPtr]::Zero) { $script:toastState = 'no toast window'; return $false }
    $r = New-Object SessionCheck.Native+RECT
    $null = [SessionCheck.Native]::GetWindowRect($toast, [ref]$r)
    $rect = [System.Drawing.Rectangle]::FromLTRB($r.Left, $r.Top, $r.Right, $r.Bottom)
    $cloaked = 0
    $null = [SessionCheck.Native]::DwmGetWindowAttribute($toast, 14, [ref]$cloaked, 4)
    $ex = [SessionCheck.Native]::GetWindowLongPtrW($toast, -20)
    $affinity = 0; $null = [SessionCheck.Native]::GetWindowDisplayAffinity($toast, [ref]$affinity)
    $covering = [SessionCheck.Native]::CoveringWindows($toast)
    $script:toastState = "visible=$([SessionCheck.Native]::IsWindowVisible($toast)) cloaked=$cloaked rect=$rect ex=0x$('{0:X}' -f $ex) affinity=0x$('{0:X}' -f $affinity) covered-by=$covering"
    if (-not [SessionCheck.Native]::OnScreen($toast)) { return $false }

    # Click-through (WS_EX_TRANSPARENT) and never activated (WS_EX_NOACTIVATE) on every show, excluded from capture, and not covered.
    $passive = ($ex -band 0x20) -ne 0 -and ($ex -band 0x8000000) -ne 0
    $inside = [bool]([System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.WorkingArea.Contains($rect) })
    return $inside -and $passive -and $affinity -eq 0x11 -and $covering -eq 0
}

# The toast's text, read through UI Automation (screen capture can't see it).
function Get-ToastText {
    $toast = [SessionCheck.Native]::Find([uint32]$process.Id, 'Screenshot Helper notification')
    if ($toast -eq [IntPtr]::Zero) { return '' }
    $condition = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    return ($A::FromHandle($toast).FindAll($TS::Descendants, $condition) | ForEach-Object { $_.Current.Name }) -join ' '
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) "sshelper-session-$([Guid]::NewGuid().ToString('N'))"
$folder = Join-Path $scratch 'shots'
New-Item -ItemType Directory -Path $folder | Out-Null
Set-Content (Join-Path $scratch 'settings.json') (@{ theme = 'Dark'; openFolderOnSessionEnd = $false } | ConvertTo-Json)
Set-Content (Join-Path $scratch 'state.json') (@{ recentFolders = @($folder) } | ConvertTo-Json)
$previousHome = $env:SCREENSHOTHELPER_HOME
$process = $null
try {
    $env:SCREENSHOTHELPER_HOME = $scratch
    $process = Start-Process $exePath -PassThru
    if (-not (Wait-Until { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero } 20000)) { throw 'The main window never appeared.' }
    $main = $process.MainWindowHandle
    Check (Wait-Until { [SessionCheck.Native]::OnScreen($main) } 2000) 'main window revealed after launch'

    $start = $A::FromHandle($main).FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Start session')))
    $start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Log ([regex]::Escape("Session started in $folder"))
    Check (Wait-Until { Test-ToastOnScreen } 1500) 'toast on screen: session started'
    Start-Sleep -Milliseconds 800

    [System.Windows.Forms.SendKeys]::SendWait('`')
    Check (Wait-Until { Test-Path (Join-Path $folder '1.png') } 5000) 'main shot saved'
    Check (Wait-Until { Test-ToastOnScreen } 1500) 'toast on screen: main shot'

    # Caption box: on screen, foreground, typing lands in it.
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('c')
    $caption = [IntPtr]::Zero
    $shown = Wait-Until { $script:caption = [SessionCheck.Native]::Find([uint32]$process.Id, 'Caption for 1'); [SessionCheck.Native]::OnScreen($script:caption) } 3000
    Check $shown 'caption box revealed'
    $focused = $shown -and (Wait-Until { [SessionCheck.Native]::GetForegroundWindow() -eq $caption } 1500)
    Check $focused 'caption box has keyboard focus'
    if ($focused) {
        [System.Windows.Forms.SendKeys]::SendWait('abc')
        Start-Sleep -Milliseconds 300
        $box = $A::FromHandle($caption).FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
        $text = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        Check ($text -eq 'abc') "typed caption landed in the box ('$text')"
    }
    if ([SessionCheck.Native]::GetForegroundWindow() -eq $caption) { [System.Windows.Forms.SendKeys]::SendWait('{ESC}') }
    Check (Wait-Until { -not [SessionCheck.Native]::OnScreen([SessionCheck.Native]::Find([uint32]$process.Id, 'Caption for 1')) } 2000) 'caption box closed'
    Start-Sleep -Milliseconds 600

    # Paused toast stays until resume; then the next shot's toast appears.
    [System.Windows.Forms.SendKeys]::SendWait('^+ ')
    Check (Wait-Until { Test-ToastOnScreen } 1500) 'toast on screen: paused'

    # A second launch while paused: it exits at once, says "already running" with every toast guard, then gives way back to "Paused".
    $second = Start-Process $exePath -PassThru
    Check ($second.WaitForExit(10000)) 'a second launch exits at once'
    Check (Wait-Until { (Get-ToastText) -like '*already running*' } 2000) 'second launch shows "already running"'
    Check (Test-ToastOnScreen) 'toast on screen: already running'
    Start-Sleep -Milliseconds 3500
    Check ((Get-ToastText) -like '*Paused*') 'toast falls back to "paused" after the warning'
    Check (Test-ToastOnScreen) 'paused toast still on screen'
    [System.Windows.Forms.SendKeys]::SendWait('^+ ')
    Start-Sleep -Milliseconds 3000
    Check (-not (Test-ToastOnScreen)) 'resumed toast hidden after its timeout'
    [System.Windows.Forms.SendKeys]::SendWait('-')
    Check (Wait-Until { Test-Path (Join-Path $folder '1-2.png') } 5000) 'sub shot saved after resume'
    Check (Wait-Until { Test-ToastOnScreen } 1500) 'toast on screen: sub shot after a pause'

    # The taskbar focused (Win+T): if it auto-hides, it slides out over the screen's bottom edge, and the toast must stay clear of it.
    [SessionCheck.Native]::keybd_event(0x5B, 0, 0, [IntPtr]::Zero); [SessionCheck.Native]::keybd_event(0x54, 0, 0, [IntPtr]::Zero)
    [SessionCheck.Native]::keybd_event(0x54, 0, 2, [IntPtr]::Zero); [SessionCheck.Native]::keybd_event(0x5B, 0, 2, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 800
    [System.Windows.Forms.SendKeys]::SendWait('`')
    Check (Wait-Until { Test-Path (Join-Path $folder '2.png') } 5000) 'main shot saved with the taskbar focused'
    Check (Wait-Until { Test-ToastOnScreen } 1500) 'toast on screen and uncovered with the taskbar focused'
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500

    [System.Windows.Forms.SendKeys]::SendWait('^+q')
    Wait-Log ([regex]::Escape('Session ended with'))
    Check (Wait-Until { [SessionCheck.Native]::OnScreen($main) } 2000) 'main window revealed with the summary'
}
finally {
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
    }
    $env:SCREENSHOTHELPER_HOME = $previousHome
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count) { Write-Host "$($failures.Count) check(s) failed."; exit 1 }
Write-Host 'All checks passed.'
