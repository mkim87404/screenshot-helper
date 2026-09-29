using ScreenshotHelper.Core.Imaging;

namespace ScreenshotHelper.Core.Tests;

public class EdgeMatteTests
{
    private const int Size = 6;
    private const int Stride = Size * 4;

    [Fact]
    public void Corner_that_only_shows_the_backdrop_becomes_fully_transparent()
    {
        var white = Fill(255, 255, 255);
        var black = Fill(0, 0, 0);

        var result = EdgeMatte.Combine(white, black, Size, Size, Stride, band: 2);

        Assert.Equal(0, Alpha(result, 0, 0));
    }

    [Fact]
    public void Half_transparent_grey_border_is_recovered_exactly()
    {
        // A 50 %-opaque pixel of colour (100, 60, 20) composites to c*0.5 + backdrop*0.5.
        var white = Fill(178, 158, 138);
        var black = Fill(50, 30, 10);

        var result = EdgeMatte.Combine(white, black, Size, Size, Stride, band: 2);

        Assert.InRange(Alpha(result, 0, 3), 126, 129);
        Assert.InRange(result[(3 * Stride) + 0], 98, 102);
        Assert.InRange(result[(3 * Stride) + 1], 58, 62);
        Assert.InRange(result[(3 * Stride) + 2], 18, 22);
    }

    [Fact]
    public void Interior_is_opaque_even_if_content_changed_between_captures()
    {
        // Simulates a video frame changing between the two captures: the interior must not become see-through.
        var white = Fill(255, 255, 255);
        var black = Fill(10, 20, 30);

        var result = EdgeMatte.Combine(white, black, Size, Size, Stride, band: 2);

        Assert.Equal(255, Alpha(result, 3, 3));
        Assert.Equal(10, result[(3 * Stride) + (3 * 4)]);
    }

    [Fact]
    public void Mismatched_buffers_are_rejected() =>
        Assert.Throws<ArgumentException>(() => EdgeMatte.Combine(new byte[10], new byte[12], Size, Size, Stride, 2));

    private static byte[] Fill(byte b, byte g, byte r)
    {
        var buffer = new byte[Stride * Size];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = b;
            buffer[i + 1] = g;
            buffer[i + 2] = r;
            buffer[i + 3] = 255;
        }

        return buffer;
    }

    private static byte Alpha(byte[] buffer, int x, int y) => buffer[(y * Stride) + (x * 4) + 3];
}
