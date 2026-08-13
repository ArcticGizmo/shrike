using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

/// <summary>
/// Spike: can ImageSharp (already a Shrike dependency) encode animated GIF / WebP frame-sequences,
/// and how big are they? This is the "encode video ourselves without a codec" path — plus a downscale
/// pass to show timeline/size levers are just frame ops.
/// </summary>
internal static class Anim
{
    public static void Check()
    {
        const int w = 1280, h = 720, frames = 60; // ~2s @ 30fps of 720p, moving content

        Console.WriteLine("== ImageSharp animated-encode probe (1280x720, 60 frames) ==");

        // Build the frame sequence (a moving box on a gradient — realistic-ish screen motion).
        var seq = new List<Image<Rgba32>>();
        for (var i = 0; i < frames; i++)
            seq.Add(MakeFrame(w, h, i, frames));

        try
        {
            TryGif("gif 720p", seq, w, h, scale: 1.0);
            TryGif("gif downscaled 640x360", seq, w, h, scale: 0.5);
            TryWebp("webp 720p", seq, w, h, scale: 1.0);
            TryWebp("webp downscaled 640x360", seq, w, h, scale: 0.5);
        }
        finally
        {
            foreach (var f in seq) f.Dispose();
        }
        Console.WriteLine();
    }

    private static void TryGif(string label, List<Image<Rgba32>> seq, int w, int h, double scale)
    {
        try
        {
            using var anim = BuildAnimation(seq, w, h, scale, delayCentis: 3);
            var path = Path.Combine(Path.GetTempPath(), $"spike-{Guid.NewGuid():N}.gif");
            anim.SaveAsGif(path, new GifEncoder());
            Report(label, path);
        }
        catch (Exception ex) { Console.WriteLine($"  {label}: FAILED {ex.Message}"); }
    }

    private static void TryWebp(string label, List<Image<Rgba32>> seq, int w, int h, double scale)
    {
        try
        {
            using var anim = BuildAnimation(seq, w, h, scale, delayCentis: 3);
            var path = Path.Combine(Path.GetTempPath(), $"spike-{Guid.NewGuid():N}.webp");
            anim.SaveAsWebp(path, new WebpEncoder { Quality = 70 });
            Report(label, path);
        }
        catch (Exception ex) { Console.WriteLine($"  {label}: FAILED {ex.Message}"); }
    }

    // Assemble a multi-frame image (optionally downscaled) with per-frame delays.
    private static Image<Rgba32> BuildAnimation(List<Image<Rgba32>> seq, int w, int h, double scale, int delayCentis)
    {
        var sw = (int)(w * scale); var sh = (int)(h * scale);
        Image<Rgba32> root = seq[0].Clone(c => { if (scale != 1.0) c.Resize(sw, sh); });
        root.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delayCentis;
        for (var i = 1; i < seq.Count; i++)
        {
            using var f = seq[i].Clone(c => { if (scale != 1.0) c.Resize(sw, sh); });
            f.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delayCentis;
            root.Frames.AddFrame(f.Frames.RootFrame);
        }
        return root;
    }

    private static Image<Rgba32> MakeFrame(int w, int h, int i, int total)
    {
        var img = new Image<Rgba32>(w, h);
        var boxX = (int)((w - 120) * (i / (double)total));
        img.ProcessPixelRows(acc =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = acc.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var inBox = x >= boxX && x < boxX + 120 && y >= h / 2 - 60 && y < h / 2 + 60;
                    row[x] = inBox
                        ? new Rgba32(245, 165, 36)
                        : new Rgba32((byte)(x % 256), (byte)(y % 256), 60);
                }
            }
        });
        return img;
    }

    private static void Report(string label, string path)
    {
        var len = new FileInfo(path).Length;
        Console.WriteLine($"  {label}: {len / 1024.0:N0} KB");
        try { File.Delete(path); } catch { }
    }
}
