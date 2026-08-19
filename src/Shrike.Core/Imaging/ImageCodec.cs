using System.Buffers.Binary;
using Shrike.Core.Capture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Shrike.Core.Imaging;

/// <summary>The file/clipboard formats Shrike can produce for a still.</summary>
public enum ImageFormatKind
{
    Png,
    Jpeg,
    WebP,
}

/// <summary>
/// Encodes a <see cref="CapturedImage"/> to bytes. PNG/JPEG/WebP go through ImageSharp (managed, no
/// native codec dependency); the Windows clipboard also wants a raw <c>CF_DIBV5</c> blob, which we
/// assemble by hand so classic apps (Office, older editors) get a pasteable bitmap alongside PNG.
/// </summary>
public static class ImageCodec
{
    public static string Extension(ImageFormatKind format) => format switch
    {
        ImageFormatKind.Png => ".png",
        ImageFormatKind.Jpeg => ".jpg",
        ImageFormatKind.WebP => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string MimeType(ImageFormatKind format) => format switch
    {
        ImageFormatKind.Png => "image/png",
        ImageFormatKind.Jpeg => "image/jpeg",
        ImageFormatKind.WebP => "image/webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Decode an encoded image (PNG/JPEG/WebP bytes) to a <see cref="CapturedImage"/> — the inverse of
    /// <see cref="Encode"/>. Used to hand a decoded video frame to the annotation editor as its base image.
    /// The result is straight, top-down BGRA at the image's native size.</summary>
    public static CapturedImage DecodeToCaptured(byte[] bytes, DateTimeOffset? capturedAt = null)
    {
        using var img = Image.Load<Bgra32>(bytes);
        var buffer = new byte[img.Width * img.Height * 4];
        img.CopyPixelDataTo(buffer);
        return new CapturedImage(img.Width, img.Height, buffer,
            new PixelBounds(0, 0, img.Width, img.Height), capturedAt ?? DateTimeOffset.Now);
    }

    /// <summary>Encode to the given format. <paramref name="quality"/> (1–100) applies to JPEG/WebP.</summary>
    public static byte[] Encode(CapturedImage image, ImageFormatKind format, int quality = 90)
    {
        var q = Math.Clamp(quality, 1, 100);
        using var img = Image.LoadPixelData<Bgra32>(image.Bgra, image.Width, image.Height);
        using var ms = new MemoryStream();

        switch (format)
        {
            case ImageFormatKind.Png:
                img.SaveAsPng(ms, new PngEncoder());
                break;
            case ImageFormatKind.Jpeg:
                img.SaveAsJpeg(ms, new JpegEncoder { Quality = q });
                break;
            case ImageFormatKind.WebP:
                img.SaveAsWebp(ms, new WebpEncoder { Quality = q });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Build a <c>CF_DIBV5</c> blob (BITMAPV5HEADER + bottom-up BGRA with an explicit alpha mask) for
    /// the Windows clipboard. Bottom-up because that's what the majority of clipboard consumers expect.
    /// </summary>
    public static byte[] ToDibV5(CapturedImage image)
    {
        const int headerSize = 124; // sizeof(BITMAPV5HEADER)
        int w = image.Width, h = image.Height, stride = w * 4;

        var buffer = new byte[headerSize + stride * h];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), headerSize);       // bV5Size
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), w);                 // bV5Width
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), h);                 // bV5Height (positive => bottom-up)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), 1);               // bV5Planes
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), 32);              // bV5BitCount
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(16), 3);               // bV5Compression = BI_BITFIELDS
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(20), (uint)(stride * h)); // bV5SizeImage
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(40), 0x00FF0000);      // bV5RedMask
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(44), 0x0000FF00);      // bV5GreenMask
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(48), 0x000000FF);      // bV5BlueMask
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(52), 0xFF000000);      // bV5AlphaMask
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(56), 0x73524742);      // bV5CSType = LCS_sRGB

        // Pixel data: rows bottom-up.
        for (var row = 0; row < h; row++)
        {
            var srcRow = (h - 1 - row) * stride;
            image.Bgra.AsSpan(srcRow, stride).CopyTo(buffer.AsSpan(headerSize + row * stride));
        }

        return buffer;
    }
}
