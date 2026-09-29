// generate-icon.cs — renders the Screenshot Helper icon (multi-size .ico + PNGs) from vector drawing code.
//
// Dependencies: .NET 10 SDK (file-based apps). SkiaSharp is pulled automatically by the #:package directive below.
// Run from the repo root:
//     dotnet run tools/generate-icon.cs
// Outputs:
//     src/ScreenshotHelper.App/Assets/app.ico        (16–256 px, PNG-compressed entries)
//     src/ScreenshotHelper.App/Assets/app-256.png    (window/tray base image)
//     .github/media/icon.png                          (README header)
#:package SkiaSharp@4.152.1

using System.Buffers.Binary;
using SkiaSharp;

var root = Directory.GetCurrentDirectory();
var assets = Path.Combine(root, "src", "ScreenshotHelper.App", "Assets");
var media = Path.Combine(root, ".github", "media");
Directory.CreateDirectory(assets);
Directory.CreateDirectory(media);

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = icoSizes.Select(size => (size, bytes: Render(size))).ToList();
File.WriteAllBytes(Path.Combine(assets, "app.ico"), BuildIco(pngs));
File.WriteAllBytes(Path.Combine(assets, "app-256.png"), Render(256));
File.WriteAllBytes(Path.Combine(media, "icon.png"), Render(256));
Console.WriteLine($"Wrote app.ico ({string.Join(", ", icoSizes)} px), app-256.png and .github/media/icon.png");

// Draws the icon on a 256-unit canvas scaled to `size`: a gradient tile, capture-frame corners, and two stacked "shots" (a group).
static byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 256f);

    // Small sizes get thicker strokes and fewer details so the silhouette stays readable at 16 px.
    var small = size <= 24;

    using (var tile = new SKPaint { IsAntialias = true })
    {
        tile.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(256, 256),
            [new SKColor(0x4F, 0x46, 0xE5), new SKColor(0x0E, 0xA5, 0xE9)], SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(new SKRect(8, 8, 248, 248), 56, 56, tile);
    }

    using (var corners = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = small ? 22 : 14, StrokeCap = SKStrokeCap.Round })
    {
        const float inset = 44, length = 42, far = 256 - inset;
        var builder = new SKPathBuilder();
        builder.MoveTo(inset, inset + length); builder.LineTo(inset, inset); builder.LineTo(inset + length, inset);
        builder.MoveTo(far - length, inset); builder.LineTo(far, inset); builder.LineTo(far, inset + length);
        builder.MoveTo(far, far - length); builder.LineTo(far, far); builder.LineTo(far - length, far);
        builder.MoveTo(inset + length, far); builder.LineTo(inset, far); builder.LineTo(inset, far - length);
        using var path = builder.Detach();
        corners.StrokeJoin = SKStrokeJoin.Round;
        canvas.DrawPath(path, corners);
    }

    // Back card (the group's earlier shot), then the front card.
    using (var back = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(small ? (byte)170 : (byte)130) })
    {
        canvas.DrawRoundRect(new SKRect(98, 78, 186, 150), 12, 12, back);
    }

    using (var front = new SKPaint { IsAntialias = true, Color = SKColors.White })
    {
        canvas.DrawRoundRect(new SKRect(72, 104, 164, 180), 12, 12, front);
    }

    if (!small)
    {
        // A mountain/sun "image" glyph on the front card.
        using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0x4F, 0x46, 0xE5) };
        var outline = new SKPathBuilder();
        outline.MoveTo(84, 168); outline.LineTo(108, 138); outline.LineTo(124, 156); outline.LineTo(136, 146); outline.LineTo(152, 168);
        outline.Close();
        using var mountain = outline.Detach();
        canvas.DrawPath(mountain, ink);
        canvas.DrawCircle(142, 124, 8, ink);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// ICO container with PNG payloads (supported since Windows Vista): 6-byte header, 16-byte directory entry per image, then the PNGs.
static byte[] BuildIco(List<(int size, byte[] bytes)> images)
{
    var headerLength = 6 + (16 * images.Count);
    var ico = new byte[headerLength + images.Sum(i => i.bytes.Length)];
    var span = ico.AsSpan();
    BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
    BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)images.Count);
    var offset = headerLength;
    for (var i = 0; i < images.Count; i++)
    {
        var (size, bytes) = images[i];
        var entry = span.Slice(6 + (16 * i), 16);
        entry[0] = (byte)(size >= 256 ? 0 : size);
        entry[1] = (byte)(size >= 256 ? 0 : size);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);
        bytes.CopyTo(span[offset..]);
        offset += bytes.Length;
    }

    return ico;
}
