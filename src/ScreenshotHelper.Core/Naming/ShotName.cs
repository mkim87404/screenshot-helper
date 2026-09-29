using System.Globalization;
using System.Text.RegularExpressions;

namespace ScreenshotHelper.Core.Naming;

/// <summary>
/// A screenshot filename split into its numeric prefix (main[-sub]) and an opaque tail that every rename preserves verbatim.
/// </summary>
public sealed partial record ShotName
{
    /// <summary>The only image extension the app writes and recognises.</summary>
    public const string PngExtension = ".png";

    private ShotName(int main, int? sub, int mainPad, int subPad, string tail, string extension)
    {
        Main = main;
        Sub = sub;
        MainPad = mainPad;
        SubPad = subPad;
        Tail = tail;
        Extension = extension;
    }

    public int Main { get; }

    /// <summary>Sub number, or null for a solo shot (e.g. <c>5.png</c>).</summary>
    public int? Sub { get; }

    /// <summary>Everything between the numbers and the extension, e.g. <c> (2026-09-27 10.05.33 UTC+13) (login)</c>.</summary>
    public string Tail { get; }

    /// <summary>Extension exactly as found on disk (casing preserved).</summary>
    public string Extension { get; }

    // Zero-padding widths are only recorded when the original number had a leading zero, so "12" renumbered to 3 becomes "3", but "05" becomes "03".
    private int MainPad { get; }
    private int SubPad { get; }

    public bool IsSolo => Sub is null;

    /// <summary>Creates a name the app is about to write (no padding).</summary>
    public static ShotName Create(int main, int? sub, string tail = "", string extension = PngExtension)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(main);
        if (sub is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sub));
        }

        return new ShotName(main, sub, 0, 0, tail, extension);
    }

    /// <summary>
    /// Parses a filename. Returns null for "foreign" files: non-PNG, no leading number, or a tail that doesn't start with a space or '('
    /// (so e.g. <c>20250101_123456.png</c> is never mistaken for group 20250101).
    /// </summary>
    public static ShotName? TryParse(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var extension = Path.GetExtension(fileName);
        if (!string.Equals(extension, PngExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stem = fileName[..^extension.Length];
        var match = PrefixRegex().Match(stem);
        if (!match.Success)
        {
            return null;
        }

        var tail = match.Groups["tail"].Value;
        if (tail.Length > 0 && tail[0] != ' ' && tail[0] != '(')
        {
            return null;
        }

        var mainText = match.Groups["main"].Value;
        if (!int.TryParse(mainText, NumberStyles.None, CultureInfo.InvariantCulture, out var main))
        {
            return null;
        }

        int? sub = null;
        var subPad = 0;
        var subGroup = match.Groups["sub"];
        if (subGroup.Success)
        {
            if (!int.TryParse(subGroup.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSub))
            {
                return null;
            }

            sub = parsedSub;
            subPad = PadWidth(subGroup.Value);
        }

        return new ShotName(main, sub, PadWidth(mainText), subPad, tail, extension);
    }

    /// <summary>Returns a copy with new numbers; tail, extension and zero-padding style are kept.</summary>
    public ShotName WithNumbers(int main, int? sub)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(main);
        if (sub is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sub));
        }

        return new ShotName(main, sub, MainPad, sub is null ? 0 : SubPad, Tail, Extension);
    }

    /// <summary>Returns a copy with a new tail (used to add or remove a timestamp/caption on a shot the app wrote); numbers are kept.</summary>
    public ShotName WithTail(string tail)
    {
        ArgumentNullException.ThrowIfNull(tail);
        return new ShotName(Main, Sub, MainPad, SubPad, tail, Extension);
    }

    /// <summary>The numbering part only, e.g. <c>5-3</c>.</summary>
    public string Numbering => Sub is null
        ? Format(Main, MainPad)
        : $"{Format(Main, MainPad)}-{Format(Sub.Value, SubPad)}";

    public string FileName => Numbering + Tail + Extension;

    public override string ToString() => FileName;

    private static string Format(int value, int pad) =>
        value.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0');

    private static int PadWidth(string digits) => digits.Length > 1 && digits[0] == '0' ? digits.Length : 0;

    // ASCII digits only: \d would also match other scripts' digits, which int.Parse rejects.
    [GeneratedRegex(@"^(?<main>[0-9]+)(?:-(?<sub>[0-9]+))?(?<tail>.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();
}
