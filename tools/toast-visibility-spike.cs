// toast-visibility-spike.cs — reproduces the app's toast window (same window settings and Win32 styles) and checks, cycle after cycle,
// whether it really appears on screen. The app's own toast is excluded from screen capture, so this copy leaves that flag off.
//
// Dependencies: .NET 10 SDK (file-based apps), Windows. Avalonia is pulled automatically by the #:package directives below.
// Run from the repo root (DISRUPTIVE, mildly: a white panel and a small toast flash in the bottom-right corner for about a minute):
//     dotnet run tools/toast-visibility-spike.cs -- layered        # the app's original styles: WS_EX_LAYERED without SetLayeredWindowAttributes
//     dotnet run tools/toast-visibility-spike.cs -- attributes     # the same plus SetLayeredWindowAttributes(alpha 255)
//     dotnet run tools/toast-visibility-spike.cs -- plain          # no WS_EX_LAYERED at all
//     add "alone" to hide the white panel (the toast is then the only window, as during a session) and use a magenta card.
// Each run shows and hides the toast 60 times (and updates it while visible 60 times), samples screen pixels inside the card, and prints
// how many cycles it was actually visible. Exit code 0 = always visible.
#:package Avalonia@12.1.3
#:package Avalonia.Desktop@12.1.3
#:package Avalonia.Themes.Fluent@12.1.3
#:property AllowUnsafeBlocks=true

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

var mode = args.FirstOrDefault() ?? "layered";
var alone = args.Contains("alone");
return AppBuilder.Configure(() => new SpikeApp(mode, alone)).UsePlatformDetect().StartWithClassicDesktopLifetime(args);

sealed class SpikeApp(string mode, bool alone) : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        Dispatcher.UIThread.Post(async () => desktop.Shutdown(await RunAsync()));
        base.OnFrameworkInitializationCompleted();
    }

    private async Task<int> RunAsync()
    {
        var screen = new Window().Screens.Primary!;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;

        // A plain white panel behind the toast's corner, so "visible" is a clear dark-on-white difference.
        var backdrop = new Window
        {
            WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, Background = Brushes.White, CanResize = false,
            Width = 600, Height = 300, WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(area.Right - (int)(600 * scale), area.Bottom - (int)(300 * scale)),
        };
        if (!alone)
        {
            backdrop.Show();
        }

        var text = new TextBlock { Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
        var toast = new Window
        {
            WindowDecorations = WindowDecorations.None, ShowInTaskbar = false, ShowActivated = false, Topmost = true, CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 460, Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Content = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), Background = new SolidColorBrush(alone ? Color.Parse("#FFFF00FF") : Color.Parse("#F21D2029")),
                BorderThickness = new Thickness(0, 0, 0, 3), BorderBrush = Brushes.LimeGreen, Child = text,
            },
        };

        var styled = false;
        var visibleShows = 0;
        var visibleUpdates = 0;
        const int Cycles = 60;
        for (var i = 1; i <= Cycles; i++)
        {
            // Show after a hide, like a new session event.
            text.Text = $"Saved {i}";
            if (!toast.IsVisible)
            {
                toast.Show();
            }

            if (!styled && toast.TryGetPlatformHandle()?.Handle is { } handle)
            {
                Native.Apply(handle, mode);
                styled = true;
            }

            await Task.Delay(40);
            Place(toast, area, scale);
            await Task.Delay(260);
            visibleShows += Native.CardVisible(toast, scale, alone) ? 1 : 0;

            // Update while still visible, like a quick second shot.
            text.Text = $"Saved {i}-2 (longer caption text {new string('x', i % 7)})";
            await Task.Delay(40);
            Place(toast, area, scale);
            await Task.Delay(260);
            visibleUpdates += Native.CardVisible(toast, scale, alone) ? 1 : 0;

            toast.Hide();
            await Task.Delay(150);
        }

        toast.Close();
        backdrop.Close();
        Console.WriteLine($"mode={mode}: visible after show {visibleShows}/{Cycles}, visible after update {visibleUpdates}/{Cycles}");
        return visibleShows == Cycles && visibleUpdates == Cycles ? 0 : 1;
    }

    private static void Place(Window toast, PixelRect area, double scale)
    {
        var width = (int)(toast.Bounds.Width * scale);
        var height = (int)(toast.Bounds.Height * scale);
        var margin = (int)(16 * scale);
        toast.Position = new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
    }
}

static partial class Native
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint LWA_ALPHA = 2;

    public static void Apply(IntPtr hwnd, string mode)
    {
        var style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT;
        if (mode != "plain")
        {
            style |= WS_EX_LAYERED;
        }

        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(style));
        if (mode == "attributes")
        {
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }
    }

    // Samples a few points inside the card; the card is near-black and the backdrop white, so any dark sample means it's drawn.
    public static bool CardVisible(Window toast, double scale, bool magenta)
    {
        var p = toast.Position;
        var w = (int)(toast.Bounds.Width * scale);
        var h = (int)(toast.Bounds.Height * scale);
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var dark = 0;
            foreach (var (fx, fy) in new[] { (0.3, 0.3), (0.5, 0.2), (0.7, 0.3), (0.85, 0.5), (0.15, 0.6) })
            {
                var c = GetPixel(dc, p.X + (int)(w * fx), p.Y + (int)(h * fy));
                var (r, g, b) = (c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF);
                dark += magenta ? (r > 200 && g < 60 && b > 200 ? 1 : 0) : (r + g + b < 200 ? 1 : 0);
            }

            return dark >= 3;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [LibraryImport("user32.dll")] private static partial IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [LibraryImport("user32.dll")] private static partial IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [LibraryImport("user32.dll")] private static partial IntPtr GetDC(IntPtr hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [LibraryImport("gdi32.dll")] private static partial uint GetPixel(IntPtr dc, int x, int y);
}
