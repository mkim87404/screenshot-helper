<#
.SYNOPSIS
    Captures a top-level window (by process name) or the primary screen to a PNG — with clean, transparent rounded corners and border.

.DESCRIPTION
    Dependencies: PowerShell 7+ on Windows (System.Drawing and WinForms are included). No modules to install.
    Run from the repo root:
        pwsh tools/Capture-Window.ps1 -ProcessName ScreenshotHelper -Output .github/media/home.png
        pwsh tools/Capture-Window.ps1 -ProcessName ScreenshotHelper -Title "Caption for 5" -Output caption.png
        pwsh tools/Capture-Window.ps1 -Output screen.png            # whole primary screen, no matting

    Why the matting: Windows 11 windows have rounded corners and a semi-transparent 1-px border, so a plain capture bakes whatever was
    behind the window (wallpaper, other apps) into the edge — it shows up as coloured noise on a README's contrasting background.
    The window is captured twice, over a white and then a black backdrop placed directly behind it, and each edge pixel's true
    opacity and colour are recovered from the difference (the same technique as the app's EdgeMatte). Only a band along the edge is
    matted; the interior comes from one capture, so animated content can't create holes.

    Privacy: this captures exactly what the window shows. Before capturing for anything published, run the app against demo data
    (e.g. SCREENSHOTHELPER_HOME + a `subst` drive with sample files) so no personal paths, names or files appear.
#>
[CmdletBinding()]
param(
    [string] $ProcessName,
    [string] $Title,
    [Parameter(Mandatory)] [string] $Output,
    [int] $DelayMs = 400
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Private.Windows.GdiPlus, System.Private.Windows.Core -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WindowShot
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetClassLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    /// Captures the window over a white and a black backdrop placed directly behind it, and mattes the edge band.
    public static Bitmap Capture(IntPtr hwnd, Rectangle bounds)
    {
        if (IsZoomed(hwnd)) return Grab(bounds);   // maximized: no corners or border to clean
        var backdrop = CreateBackdrop();
        try
        {
            using (var white = Over(backdrop, hwnd, bounds, 0))    // WHITE_BRUSH
            using (var black = Over(backdrop, hwnd, bounds, 4))    // BLACK_BRUSH
            {
                var dpi = GetDpiForWindow(hwnd);
                var band = (int)Math.Ceiling(12 * (dpi == 0 ? 1.0 : dpi / 96.0));
                return Matte(white, black, band);
            }
        }
        finally { DestroyWindow(backdrop); }
    }

    public static Bitmap Grab(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
        return bmp;
    }

    /// A never-activated popup whose window procedure is the system's DefWindowProc, painting the class background brush.
    static IntPtr CreateBackdrop()
    {
        var instance = GetModuleHandleW(null);
        var cls = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW)),
            lpfnWndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
            hInstance = instance,
            hbrBackground = GetStockObject(0),
            lpszClassName = Marshal.StringToHGlobalUni("CaptureWindowBackdrop"),
        };
        RegisterClassExW(ref cls);   // harmlessly fails if already registered in this process
        // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, WS_POPUP
        return CreateWindowExW(0x80 | 0x08000000, "CaptureWindowBackdrop", "", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    static Bitmap Over(IntPtr backdrop, IntPtr target, Rectangle bounds, int stockBrush)
    {
        SetClassLongPtrW(backdrop, -10, GetStockObject(stockBrush));   // GCLP_HBRBACKGROUND
        SetWindowPos(backdrop, target, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);   // behind target, no activate, show
        InvalidateRect(backdrop, IntPtr.Zero, true);
        UpdateWindow(backdrop);
        MSG msg;
        while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
        DwmFlush(); DwmFlush();
        return Grab(bounds);
    }

    /// Edge band: alpha = 255 − mean(white − black); colour = black ÷ alpha. Interior: opaque from the black capture.
    static Bitmap Matte(Bitmap white, Bitmap black, int band)
    {
        int w = white.Width, h = white.Height, stride = w * 4;
        var W = Read(white); var B = Read(black); var R = new byte[W.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            bool edge = x < band || y < band || x >= w - band || y >= h - band;
            int a = edge ? Math.Max(0, Math.Min(255, 255 - (W[i] - B[i] + W[i + 1] - B[i + 1] + W[i + 2] - B[i + 2]) / 3)) : 255;
            R[i + 3] = (byte)a;
            if (a == 0) continue;
            for (int c = 0; c < 3; c++) R[i + c] = (byte)Math.Min(255, edge ? B[i + c] * 255 / a : B[i + c]);
        }
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < h; y++) Marshal.Copy(R, y * stride, data.Scan0 + y * data.Stride, stride); }
        finally { result.UnlockBits(data); }
        return result;
    }

    static byte[] Read(Bitmap bmp)
    {
        int stride = bmp.Width * 4; var buf = new byte[stride * bmp.Height];
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { for (int y = 0; y < bmp.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * stride, stride); }
        finally { bmp.UnlockBits(data); }
        return buf;
    }
}
'@

# Per-monitor-V2 (-4) so window bounds and captures use physical pixels.
[WindowShot]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null

if ($ProcessName) {
    $pids = @(Get-Process -Name $ProcessName -ErrorAction Stop | ForEach-Object Id)
    $script:found = [IntPtr]::Zero
    $callback = [WindowShot+EnumWindowsProc] {
        param($hwnd, $lParam)
        [uint32] $windowPid = 0
        [WindowShot]::GetWindowThreadProcessId($hwnd, [ref] $windowPid) | Out-Null
        if ($pids -contains $windowPid -and [WindowShot]::IsWindowVisible($hwnd)) {
            $text = [System.Text.StringBuilder]::new(256)
            [WindowShot]::GetWindowText($hwnd, $text, 256) | Out-Null
            if ($text.Length -gt 0 -and (-not $Title -or $text.ToString() -eq $Title)) {
                $script:found = $hwnd
                return $false
            }
        }
        return $true
    }
    [WindowShot]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null
    if ($script:found -eq [IntPtr]::Zero) { throw "No visible window found for process '$ProcessName'$(if ($Title) { " titled '$Title'" })." }

    [WindowShot]::SetForegroundWindow($script:found) | Out-Null
    Start-Sleep -Milliseconds $DelayMs
    $rect = [WindowShot+RECT]::new()
    # DWMWA_EXTENDED_FRAME_BOUNDS (9): the visible frame without the invisible resize border.
    [WindowShot]::DwmGetWindowAttribute($script:found, 9, [ref] $rect, 16) | Out-Null
    $bounds = [System.Drawing.Rectangle]::FromLTRB($rect.Left, $rect.Top, $rect.Right, $rect.Bottom)
    $bitmap = [WindowShot]::Capture($script:found, $bounds)
}
else {
    Start-Sleep -Milliseconds $DelayMs
    $bitmap = [WindowShot]::Grab([System.Windows.Forms.Screen]::PrimaryScreen.Bounds)
}

try {
    $outputPath = [System.IO.Path]::GetFullPath($Output)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($outputPath)) | Out-Null
    $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output "Saved $($bitmap.Width)x$($bitmap.Height) to $outputPath"
}
finally {
    $bitmap.Dispose()
}
