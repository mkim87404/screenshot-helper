<#
.SYNOPSIS
    Films a screen region while Screenshot Helper launches (or while you trigger a transition), so flashes, blank frames and window jumps
    can be seen frame by frame instead of guessed at.

.DESCRIPTION
    Dependencies: PowerShell 7+ on Windows (uses the built-in System.Drawing and UI Automation assemblies). No modules to install.
    Run from the repo root, with Screenshot Helper closed:
        pwsh tools/Record-Frames.ps1 -Exe 'src/ScreenshotHelper.App/bin/Release/net10.0-windows/Screenshot Helper.exe'
        pwsh tools/Record-Frames.ps1 -Exe '…\Screenshot Helper.exe' -Theme Light -SaveFrames out/frames
        pwsh tools/Record-Frames.ps1 -AttachTo 'Screenshot Helper' -Seconds 3   # film a running instance while you (or a script) act

    DISRUPTIVE (mildly): the app's window opens and closes on your desktop. No keys are sent.

    With -AttachTo it films an already-running process instead of launching one (for theme switches, prompts and the like).
    Otherwise it launches the exe against a scratch SCREENSHOTHELPER_HOME seeded with the chosen theme, captures the centre of the primary screen
    (where the main window opens) as fast as GDI allows for -Seconds, then closes the app. For every frame it prints the time since
    launch, the app window's on-screen rectangle (DWM extended frame bounds), and the average brightness (0–255) of three bands of the
    window: title bar, page and bottom. A dark-theme launch should never show a bright frame and the rectangle should never change.
    Captures use BitBlt, which sees what DWM composes, so cloaked or not-yet-drawn windows look exactly as the user would see them.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Launch')] [string] $Exe,
    [Parameter(Mandatory, ParameterSetName = 'Attach')] [string] $AttachTo,
    [ValidateSet('Light', 'Dark')] [string] $Theme = 'Dark',
    [ValidateRange(0.5, 10)] [double] $Seconds = 2.5,
    [string] $SaveFrames
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSCmdlet.ParameterSetName -eq 'Launch') {
    $exePath = (Resolve-Path -LiteralPath $Exe).Path
    $processName = [IO.Path]::GetFileNameWithoutExtension($exePath)
    # Any running instance (whatever its exe is called) holds this object; a new launch would only activate it and exit.
    if ([System.Threading.Mutex]::TryOpenExisting('Local\ScreenshotHelper.SingleInstance', [ref]$null)) { throw 'Close Screenshot Helper first (tray icon → Exit).' }
}

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Private.Windows.GdiPlus, System.Private.Windows.Core, System.Collections -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

public sealed class FrameRecorder
{
    public sealed class Frame { public double Ms; public Bitmap Image; public Rectangle Window; }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    // The largest visible, uncloaked top-level window of the process, by DWM's extended frame bounds (DWMWA_EXTENDED_FRAME_BOUNDS = 9).
    public static Rectangle WindowOf(uint pid)
    {
        Rectangle best = Rectangle.Empty;
        EnumWindows((h, l) =>
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            int cloaked;
            RECT r;
            if (owner == pid && IsWindowVisible(h) && (DwmGetWindowAttribute(h, 14, out cloaked, 4) != 0 || cloaked == 0)
                && DwmGetWindowAttribute(h, 9, out r, 16) == 0)
            {
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (rect.Width * rect.Height > best.Width * best.Height) best = rect;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static List<Frame> Record(Rectangle region, uint pid, Stopwatch clock, double seconds)
    {
        var frames = new List<Frame>();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(region.Location, Point.Empty, region.Size);
            frames.Add(new Frame { Ms = clock.Elapsed.TotalMilliseconds, Image = bmp, Window = WindowOf(pid) });
        }
        return frames;
    }

    // Mean brightness of a horizontal band of the window (fractions of its height), sampled on a grid.
    public static double Brightness(Bitmap image, Rectangle window, Rectangle region, double top, double bottom)
    {
        var area = Rectangle.Intersect(window, region);
        if (area.IsEmpty) return -1;
        double sum = 0; int n = 0;
        int y0 = area.Top + (int)(area.Height * top), y1 = area.Top + (int)(area.Height * bottom);
        for (int y = y0; y < y1; y += 6)
            for (int x = area.Left + 4; x < area.Right - 4; x += 12)
            {
                var c = image.GetPixel(x - region.Left, y - region.Top);
                sum += 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B; n++;
            }
        return n == 0 ? -1 : sum / n;
    }
}
'@

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$width = [math]::Min($screen.Width, 2200); $height = [math]::Min($screen.Height, 1900)
$region = [System.Drawing.Rectangle]::new($screen.X + [int](($screen.Width - $width) / 2), $screen.Y + [int](($screen.Height - $height) / 2), $width, $height)

$scratch = $null
$previousHome = $env:SCREENSHOTHELPER_HOME
$process = $null
try {
    if ($AttachTo) {
        $target = @(Get-Process -Name $AttachTo -ErrorAction Stop)[0]
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $frames = [FrameRecorder]::Record($region, [uint32]$target.Id, $clock, $Seconds)
    }
    else {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) "sshelper-frames-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $scratch | Out-Null
        Set-Content (Join-Path $scratch 'settings.json') (@{ theme = $Theme } | ConvertTo-Json)
        $env:SCREENSHOTHELPER_HOME = $scratch
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath $exePath -PassThru
        $frames = [FrameRecorder]::Record($region, [uint32]$process.Id, $clock, $Seconds)
    }

    Write-Host ("{0} frames in {1:N1} s (about {2:N0} ms apart, times since recording began). Brightness: title / page / bottom (−1 = window not on screen)." -f `
        $frames.Count, $Seconds, ($Seconds * 1000 / [math]::Max(1, $frames.Count)))
    $last = $null
    foreach ($f in $frames) {
        if ($f.Window.IsEmpty) { $line = 'no window' }
        else {
            $title = [FrameRecorder]::Brightness($f.Image, $f.Window, $region, 0.0, 0.035)
            $page = [FrameRecorder]::Brightness($f.Image, $f.Window, $region, 0.2, 0.8)
            $bottom = [FrameRecorder]::Brightness($f.Image, $f.Window, $region, 0.9, 1.0)
            $line = '{0},{1} {2}x{3}  {4,5:N0} {5,5:N0} {6,5:N0}' -f $f.Window.X, $f.Window.Y, $f.Window.Width, $f.Window.Height, $title, $page, $bottom
        }
        # Print only changes, so a steady window is one line.
        if ($line -ne $last) { Write-Host ('{0,7:N0} ms  {1}' -f $f.Ms, $line); $last = $line }
    }

    if ($SaveFrames) {
        New-Item -ItemType Directory -Force -Path $SaveFrames | Out-Null
        $i = 0
        foreach ($f in $frames) { $f.Image.Save((Join-Path $SaveFrames ('{0:D3}-{1:N0}ms.png' -f $i++, $f.Ms)), [System.Drawing.Imaging.ImageFormat]::Png) }
        Write-Host "Saved $i frames to $SaveFrames"
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force }
    }
    $env:SCREENSHOTHELPER_HOME = $previousHome
    if ($scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}
