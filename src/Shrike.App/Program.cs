using Avalonia;
using Shrike.App.Services;
using Shrike.Core.Capture;
using Shrike.Core.Imaging;
using Shrike.Core.Ipc;
using Shrike.Core.Startup;

namespace Shrike.App;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any SynchronizationContext-
    // reliant code before AppMain is called: things aren't initialized yet and stuff will break.
    [STAThread]
    public static int Main(string[] args)
    {
        // First line: start the snappy-load clock so every later mark is measured from process entry.
        var budget = StartupBudget.Start();

        // Headless diagnostic: capture the whole virtual screen, encode each format, report sizes.
        // Proves the capture→encode pipeline without any UI. (Writes to the given dir, default CWD.)
        if (args.Length > 0 && string.Equals(args[0], "capture-test", StringComparison.OrdinalIgnoreCase))
            return RunCaptureTest(args.Length > 1 ? args[1] : ".");

        var measure = args.Length > 0
            && string.Equals(args[0], "measure-startup", StringComparison.OrdinalIgnoreCase);

        // Single-instance guard. A normal second launch forwards its intent to the resident instance
        // over the pipe and exits — no cold start. A measure run boots its own throwaway instance.
        var single = SingleInstance.Acquire();
        if (!single.IsPrimary && !measure)
        {
            SingleInstance.SendToPrimary(IpcProtocol.ActionFromArgs(args));
            single.Dispose();
            return 0;
        }

        AppEnv.Budget = budget;
        AppEnv.SingleInstance = single.IsPrimary ? single : null;
        AppEnv.MeasureMode = measure;

        try
        {
            // Avalonia gets no CLI args — our own verbs (measure-startup) are handled above.
            BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        finally
        {
            single.Dispose();
        }

        return 0;
    }

    private static int RunCaptureTest(string outputDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("capture-test requires Windows.");
            return 1;
        }

        var bounds = ScreenCapture.VirtualScreenBounds();
        var image = ScreenCapture.Capture(bounds);
        Directory.CreateDirectory(outputDir);

        var stem = CaptureNaming.Expand(CaptureNaming.DefaultTemplate, image.CapturedAt);
        Console.WriteLine($"captured {image.Width}x{image.Height} from {bounds.X},{bounds.Y}");

        foreach (var format in Enum.GetValues<ImageFormatKind>())
        {
            var bytes = ImageCodec.Encode(image, format);
            var path = Path.Combine(outputDir, stem + ImageCodec.Extension(format));
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"  {format,-5} {bytes.Length,9:n0} bytes -> {path}");
        }

        Console.WriteLine($"  DIBv5 {ImageCodec.ToDibV5(image).Length,9:n0} bytes (clipboard blob)");
        return 0;
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
