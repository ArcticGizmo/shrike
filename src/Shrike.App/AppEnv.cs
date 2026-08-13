using Shrike.App.Services;
using Shrike.Core.Startup;

namespace Shrike.App;

/// <summary>
/// Process-wide handoff from <see cref="Program"/> to <see cref="App"/>. Deliberately tiny — M0 has
/// no DI container; these are the few singletons the Avalonia app needs that are created before the
/// framework starts.
/// </summary>
internal static class AppEnv
{
    /// <summary>The startup clock, started on the first line of <c>Main</c>.</summary>
    public static StartupBudget? Budget { get; set; }

    /// <summary>The single-instance handle when this process is the primary; null otherwise.</summary>
    public static SingleInstance? SingleInstance { get; set; }

    /// <summary>True for a <c>measure-startup</c> diagnostic run — boots, prints timings, exits.</summary>
    public static bool MeasureMode { get; set; }
}
