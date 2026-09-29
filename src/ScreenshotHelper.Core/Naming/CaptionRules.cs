using System.Text;

namespace ScreenshotHelper.Core.Naming;

/// <summary>Validation, sanitising and length budgeting for filename captions (Windows filename rules, regardless of host OS).</summary>
public static class CaptionRules
{
    /// <summary>Windows MAX_PATH (260) minus the terminating NUL. Long paths are deliberately treated as disabled.</summary>
    public const int MaxPathLength = 259;

    /// <summary>Typical NTFS per-component limit.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>
    /// Characters kept free for later renames of the same file: solo→member adds "-1" (2), and Renumber may add up to 3 digits to each number (6).
    /// </summary>
    public const int GrowthReserve = 8;

    /// <summary>The <c> (</c> and <c>)</c> wrapped around a caption.</summary>
    private const int CaptionWrapperLength = 3;

    private const string InvalidCharacters = "<>:\"/\\|?*";

    /// <summary>Characters the caption box refuses, for display in its hint text.</summary>
    public static string InvalidCharactersDisplay => "< > : \" / \\ | ? *";

    public static bool IsAllowed(char c) => c >= 32 && c != 127 && !InvalidCharacters.Contains(c, StringComparison.Ordinal);

    /// <summary>Removes characters that can't appear in a Windows filename (used for pasted text).</summary>
    public static string RemoveInvalid(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (IsAllowed(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>The caption as it will appear in the filename: invalid characters removed, whitespace trimmed; null when nothing remains.</summary>
    public static string? Normalize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = RemoveInvalid(text).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// How many caption characters fit for the next shot in <paramref name="folder"/>.
    /// Assumes the worst case for everything not yet decided: the longest numbering either key could produce and a timestamp segment,
    /// so text the user types is never truncated later.
    /// </summary>
    public static int Budget(string folder, int worstCaseNumberingLength) =>
        BudgetForName(folder, worstCaseNumberingLength + ShotTail.MaxTimestampLength + CaptionWrapperLength + ShotName.PngExtension.Length);

    /// <summary>
    /// How many caption characters fit in a file whose name, without any caption, is <paramref name="nameLengthWithoutCaption"/> long
    /// (include room for a timestamp there if one may still be added). Both the 255-character name and the 260-character path limits
    /// apply, less <see cref="GrowthReserve"/>.
    /// </summary>
    public static int BudgetForName(string folder, int nameLengthWithoutCaption)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var fixedName = nameLengthWithoutCaption + CaptionWrapperLength + GrowthReserve;
        var folderPrefix = Path.TrimEndingDirectorySeparator(folder).Length + 1;
        var byName = MaxFileNameLength - fixedName;
        var byPath = MaxPathLength - folderPrefix - fixedName;
        return Math.Max(0, Math.Min(byName, byPath));
    }

    /// <summary>
    /// Truncates an already-normalised caption so <paramref name="fileNameWithoutCaption"/> plus the caption fits both limits.
    /// A safety net only: the caption box already enforces <see cref="Budget"/>.
    /// </summary>
    public static string? FitToPath(string folder, string fileNameWithoutCaption, string? caption)
    {
        if (caption is null)
        {
            return null;
        }

        var folderPrefix = Path.TrimEndingDirectorySeparator(folder).Length + 1;
        var available = Math.Min(
            MaxFileNameLength - fileNameWithoutCaption.Length - CaptionWrapperLength,
            MaxPathLength - folderPrefix - fileNameWithoutCaption.Length - CaptionWrapperLength);
        if (available <= 0)
        {
            return null;
        }

        if (caption.Length <= available)
        {
            return caption;
        }

        // Windows limits count UTF-16 code units; never cut an emoji/surrogate pair in half.
        if (char.IsHighSurrogate(caption[available - 1]))
        {
            available--;
        }

        var cut = caption[..available].TrimEnd();
        return cut.Length == 0 ? null : cut;
    }
}
