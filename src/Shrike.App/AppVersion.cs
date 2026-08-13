using System.Reflection;

namespace Shrike.App;

/// <summary>The running app version, as a display string (e.g. <c>0.1.0</c>). Read from the assembly's
/// informational version, with any <c>+build</c> suffix trimmed. Shared by the About window and the
/// changelog "what's new" flow so they always agree on the version.</summary>
internal static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
            return info.Split('+')[0];
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
