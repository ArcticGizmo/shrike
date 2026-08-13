using System.Drawing;
using System.Drawing.Imaging;
using Svg;

// Generates every raster icon asset from the single source-of-truth SVG (shrike.svg).
//
//   src/Shrike.App/Assets/icon.png    256x256 PNG — window icons and the in-app logo
//   src/Shrike.App/Assets/shrike.ico  multi-resolution ICO (16..256) — tray + the .exe ApplicationIcon
//   shrike-icon.png                   512x512 PNG — the README header logo
//
// Re-run after editing shrike.svg:  dotnet run --project tools/IconGen
// (or tools/gen-icons.ps1, which also restores packages.)

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string svgPath = Path.Combine(repoRoot, "shrike.svg");
if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"Source SVG not found: {svgPath}");
    return 1;
}

Console.WriteLine($"Source: {svgPath}");
var doc = SvgDocument.Open(svgPath);

// The .ico ships a true frame at each size so Windows never downscales at runtime (tray asks 16px;
// large surfaces ask 256).
int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };

string assetsDir = Path.Combine(repoRoot, "src", "Shrike.App", "Assets");
WritePng(doc, Path.Combine(assetsDir, "icon.png"), 256);
WritePng(doc, Path.Combine(repoRoot, "shrike-icon.png"), 512);
WriteIco(doc, Path.Combine(assetsDir, "shrike.ico"), icoSizes);

Console.WriteLine("Done.");
return 0;

// Renders the whole viewBox into a size×size transparent bitmap. Setting the document's width/height to
// pixels makes the library map the viewBox (in user units) straight onto the pixel canvas — the SVG here
// declares its size in mm, so we must override that or the content renders at the wrong scale.
static Bitmap Render(SvgDocument doc, int size)
{
    doc.Width = new SvgUnit(SvgUnitType.Pixel, size);
    doc.Height = new SvgUnit(SvgUnitType.Pixel, size);
    return doc.Draw();
}

static void WritePng(SvgDocument doc, string path, int size)
{
    using var bmp = Render(doc, size);
    bmp.Save(path, ImageFormat.Png);
    Console.WriteLine($"  {Path.GetFileName(path)}  {size}x{size}");
}

// Writes a Vista+ ICO whose frames are PNG-compressed (keeps the file small and supports 256px).
static void WriteIco(SvgDocument doc, string path, int[] sizes)
{
    var frames = new List<byte[]>();
    foreach (var size in sizes)
    {
        using var bmp = Render(doc, size);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        frames.Add(ms.ToArray());
    }

    using var fs = File.Create(path);
    using var w = new BinaryWriter(fs);

    // ICONDIR header
    w.Write((ushort)0);             // reserved
    w.Write((ushort)1);             // type = icon
    w.Write((ushort)sizes.Length);  // image count

    int offset = 6 + sizes.Length * 16;   // ICONDIRENTRY is 16 bytes; image data follows the directory
    for (int i = 0; i < sizes.Length; i++)
    {
        int size = sizes[i];
        w.Write((byte)(size >= 256 ? 0 : size)); // width  (0 = 256)
        w.Write((byte)(size >= 256 ? 0 : size)); // height (0 = 256)
        w.Write((byte)0);                        // palette count
        w.Write((byte)0);                        // reserved
        w.Write((ushort)1);                      // colour planes
        w.Write((ushort)32);                     // bits per pixel
        w.Write(frames[i].Length);               // bytes of image data
        w.Write(offset);                         // offset of image data
        offset += frames[i].Length;
    }

    foreach (var frame in frames)
        w.Write(frame);

    Console.WriteLine($"  {Path.GetFileName(path)}  [{string.Join(", ", sizes)}]");
}

// Walks up from the tool's binary location to the directory that holds shrike.svg.
static string FindRepoRoot(string start)
{
    var dir = new DirectoryInfo(start);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "shrike.svg")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return Path.GetFullPath(Path.Combine(start, "..", "..", "..", "..", ".."));
}
