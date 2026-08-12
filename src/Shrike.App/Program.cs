using Avalonia;
using Shrike.App.Services;
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

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
