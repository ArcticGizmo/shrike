using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shrike.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON under <c>%APPDATA%\Shrike\settings.json</c> (a dev
/// build uses <c>%APPDATA%\Shrike (Dev)\settings.json</c> — see <see cref="AppProfile"/>). Loading is
/// deliberately forgiving: a missing file, unreadable file, or malformed JSON all fall back to
/// <see cref="AppSettings.Default"/> rather than throwing — settings should never stop the app from
/// starting. Values are clamped on load. The path is injectable so the whole thing is headless-testable.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Path { get; }

    public SettingsStore(string? path = null) => Path = path ?? DefaultPath();

    /// <summary>The canonical location: <c>%APPDATA%\Shrike\settings.json</c> (or the <c>Shrike (Dev)</c>
    /// folder for a dev build, so it never reads/writes the installed release's settings).</summary>
    public static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return System.IO.Path.Combine(appData, AppProfile.DataFolderName, "settings.json");
    }

    /// <summary>Read settings, or the defaults if the file is absent/unreadable/corrupt.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return AppSettings.Default;
            var json = File.ReadAllText(Path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Json);
            return (loaded ?? AppSettings.Default).Sanitised();
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    /// <summary>Write settings, creating the folder if needed. Returns false if it couldn't be saved.</summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(settings.Sanitised(), Json));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
