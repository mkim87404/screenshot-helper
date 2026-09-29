namespace ScreenshotHelper.Core.Imaging;

/// <summary>
/// Recovers true transparency along a window's edge from two captures of the same area, one over a white backdrop and one over black
/// ("difference matting"). Windows 11 windows have rounded corners and a semi-transparent 1-px border; a single capture bakes whatever
/// was behind them into the edge pixels, which shows up as coloured noise on a contrasting background. With two backdrops, each pixel's
/// opacity is 1 − (white − black)/255, and its colour is the black capture divided by that opacity.
/// </summary>
public static class EdgeMatte
{
    /// <summary>
    /// Builds a BGRA (premultiplied = false) image from two BGRA captures of identical size. Only a band of <paramref name="band"/> pixels
    /// along the edges is matted; the interior is copied opaque from <paramref name="overBlack"/>, so content that changed between the two
    /// captures (video, a blinking caret) can never turn into spurious transparency.
    /// </summary>
    public static byte[] Combine(ReadOnlySpan<byte> overWhite, ReadOnlySpan<byte> overBlack, int width, int height, int stride, int band)
    {
        if (overWhite.Length != overBlack.Length || overWhite.Length < stride * height || stride < width * 4)
        {
            throw new ArgumentException("Both captures must be BGRA buffers of the same size.");
        }

        var result = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                var inBand = x < band || y < band || x >= width - band || y >= height - band;
                if (!inBand)
                {
                    result[i] = overBlack[i];
                    result[i + 1] = overBlack[i + 1];
                    result[i + 2] = overBlack[i + 2];
                    result[i + 3] = 255;
                    continue;
                }

                // Opacity from how much the backdrop shows through, averaged over the three channels.
                var diff = (overWhite[i] - overBlack[i] + overWhite[i + 1] - overBlack[i + 1] + overWhite[i + 2] - overBlack[i + 2]) / 3;
                var alpha = Math.Clamp(255 - diff, 0, 255);
                result[i + 3] = (byte)alpha;
                if (alpha == 0)
                {
                    continue;
                }

                // Over black, the captured colour is colour × alpha; divide it back out.
                result[i] = (byte)Math.Min(255, overBlack[i] * 255 / alpha);
                result[i + 1] = (byte)Math.Min(255, overBlack[i + 1] * 255 / alpha);
                result[i + 2] = (byte)Math.Min(255, overBlack[i + 2] * 255 / alpha);
            }
        }

        return result;
    }
}
