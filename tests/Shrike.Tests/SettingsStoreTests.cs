using Shrike.Core.Imaging;
using Shrike.Core.Settings;

namespace Shrike.Tests;

public class SettingsStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"shrike-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void Missing_file_loads_defaults()
    {
        var store = new SettingsStore(TempFile());
        Assert.Equal(AppSettings.Default, store.Load());
    }

    [Fact]
    public void Save_then_load_round_trips_every_field()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings
            {
                CaptureHotkey = "Ctrl+Alt+P",
                RecordHotkey = "",
                DesktopBehaviour = DesktopBehaviour.NewWindowHere,
                RingSize = 25,
                RingByteCap = 256L * 1024 * 1024,
                DefaultSaveDirectory = @"C:\shots",
                DefaultImageFormat = ImageFormatKind.Jpeg,
                CursorInRecording = false,
                Autostart = true,
            };

            Assert.True(store.Save(settings));
            Assert.Equal(settings, store.Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_json_falls_back_to_defaults()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ this is not valid json ][");
            Assert.Equal(AppSettings.Default, new SettingsStore(path).Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Partial_json_keeps_defaults_for_absent_fields()
    {
        var path = TempFile();
        try
        {
            // Only one field present — everything else must default.
            File.WriteAllText(path, "{ \"CaptureHotkey\": \"Win+S\" }");
            var loaded = new SettingsStore(path).Load();

            Assert.Equal("Win+S", loaded.CaptureHotkey);
            Assert.Equal(AppSettings.Default.RingSize, loaded.RingSize);
            Assert.Equal(AppSettings.Default.DefaultImageFormat, loaded.DefaultImageFormat);
            Assert.False(loaded.Autostart);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_clamps_out_of_range_values()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ \"RingSize\": 9999 }");
            Assert.Equal(100, new SettingsStore(path).Load().RingSize);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Default_path_is_under_appdata_shrike()
    {
        var p = SettingsStore.DefaultPath();
        Assert.EndsWith(Path.Combine("Shrike", "settings.json"), p);
    }
}
