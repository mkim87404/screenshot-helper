using System.Globalization;

namespace ScreenshotHelper.Core.Naming;

/// <summary>Which clock the filename timestamp uses.</summary>
public enum TimestampZone
{
    Local,
    Utc,
}

/// <summary>Builds the optional <c> (timestamp) (caption)</c> tail of a new screenshot filename.</summary>
public static class ShotTail
{
    /// <summary>Longest possible timestamp text, e.g. <c>2026-09-27 10.05.33 UTC+12.45</c>; used for caption length budgeting.</summary>
    public const int MaxTimestampLength = 29;

    public static string Build(string? timestamp, string? caption)
    {
        var tail = string.Empty;
        if (!string.IsNullOrEmpty(timestamp))
        {
            tail += $" ({timestamp})";
        }

        if (!string.IsNullOrEmpty(caption))
        {
            tail += $" ({caption})";
        }

        return tail;
    }

    /// <summary>
    /// Formats a capture time as a filename-safe timestamp with second precision:
    /// <c>2026-09-27 10.05.33 UTC+13</c> (local, offset taken from the OS so daylight saving is right) or <c>2026-09-26 21.05.33 UTC</c>.
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset capturedAt, TimestampZone zone)
    {
        // Dots instead of colons: ':' is invalid in Windows filenames. Invariant culture keeps digits/separators stable across locales.
        if (zone == TimestampZone.Utc)
        {
            return capturedAt.UtcDateTime.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + " UTC";
        }

        return capturedAt.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + " " + FormatOffset(capturedAt.Offset);
    }

    /// <summary>Formats a UTC offset as <c>UTC+13</c>, <c>UTC+5.30</c>, <c>UTC-3.30</c> or <c>UTC+0</c>.</summary>
    public static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        var hours = (int)abs.TotalHours;
        return abs.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{hours}.{abs.Minutes:00}");
    }
}
