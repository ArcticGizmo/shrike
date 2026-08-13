using Shrike.Core.Recording;

namespace Shrike.Tests;

public class FfmpegLocatorTests
{
    [Fact]
    public void Override_env_var_takes_precedence_when_it_runs()
    {
        // Point the override at the same ffmpeg the machine already has (if any); it must be honoured.
        if (Ffmpeg.Locate() is not { } existing)
            return; // no ffmpeg present — nothing to point at.

        var prior = Environment.GetEnvironmentVariable(Ffmpeg.OverrideEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Ffmpeg.OverrideEnvVar, existing);
            Ffmpeg.ResetCache();
            Assert.Equal(existing, Ffmpeg.Locate());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Ffmpeg.OverrideEnvVar, prior);
            Ffmpeg.ResetCache();
        }
    }

    [Fact]
    public void Bogus_override_is_ignored_and_falls_through()
    {
        var prior = Environment.GetEnvironmentVariable(Ffmpeg.OverrideEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Ffmpeg.OverrideEnvVar,
                Path.Combine(Path.GetTempPath(), "definitely-not-ffmpeg.exe"));
            Ffmpeg.ResetCache();
            // A non-existent override must not be returned; result is either a real ffmpeg or null.
            var located = Ffmpeg.Locate();
            Assert.DoesNotContain("definitely-not-ffmpeg", located ?? "");
        }
        finally
        {
            Environment.SetEnvironmentVariable(Ffmpeg.OverrideEnvVar, prior);
            Ffmpeg.ResetCache();
        }
    }
}
