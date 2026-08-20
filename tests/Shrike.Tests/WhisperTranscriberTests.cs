using Shrike.Core.Recording;

namespace Shrike.Tests;

public class WhisperTranscriberTests
{
    // --- JSON transcript parsing (no binary needed) -------------------------------------------------------

    [Fact]
    public void ParseJson_reads_offset_segments_and_trims_text()
    {
        const string json = """
        {
          "result": { "language": "en" },
          "transcription": [
            { "offsets": { "from": 0,    "to": 1500 }, "text": " Hello there. " },
            { "offsets": { "from": 1500, "to": 4000 }, "text": "General Kenobi." },
            { "offsets": { "from": 4000, "to": 4000 }, "text": "zero length, dropped" },
            { "offsets": { "from": 5000, "to": 6000 }, "text": "   " }
          ]
        }
        """;

        var cues = WhisperTranscriber.ParseJson(json);

        Assert.Equal(2, cues.Count);
        Assert.Equal(new CaptionCue(0, 1500, "Hello there."), cues[0]); // text trimmed
        Assert.Equal(new CaptionCue(1500, 4000, "General Kenobi."), cues[1]);
    }

    [Fact]
    public void ParseJson_falls_back_to_timestamp_strings_when_offsets_absent()
    {
        const string json = """
        {
          "transcription": [
            { "timestamps": { "from": "00:00:01,500", "to": "00:00:02.750" }, "text": "mixed separators" }
          ]
        }
        """;

        var cue = Assert.Single(WhisperTranscriber.ParseJson(json));
        Assert.Equal(1500, cue.StartMs);
        Assert.Equal(2750, cue.EndMs);
        Assert.Equal("mixed separators", cue.Text);
    }

    [Fact]
    public void ParseJson_is_tolerant_of_junk_and_missing_fields()
    {
        Assert.Empty(WhisperTranscriber.ParseJson(""));
        Assert.Empty(WhisperTranscriber.ParseJson("not json at all"));
        Assert.Empty(WhisperTranscriber.ParseJson("{}"));                       // no transcription array
        Assert.Empty(WhisperTranscriber.ParseJson("""{ "transcription": {} }""")); // wrong shape
        Assert.Empty(WhisperTranscriber.ParseJson("""{ "transcription": [ { "text": "no times" } ] }"""));
    }

    // --- pure arg builders --------------------------------------------------------------------------------

    [Fact]
    public void ResampleArgs_targets_16k_mono_pcm()
    {
        var args = WhisperTranscriber.ResampleArgs("in.wav", "out.wav");
        Assert.Contains("16000", args);
        AssertPairInOrder(args, "-ar", "16000");
        AssertPairInOrder(args, "-ac", "1");
        AssertPairInOrder(args, "-c:a", "pcm_s16le");
        AssertPairInOrder(args, "-i", "in.wav");
        Assert.Equal("out.wav", args[^1]); // output is last
    }

    [Fact]
    public void WhisperArgs_requests_json_output_language_and_progress()
    {
        var args = WhisperTranscriber.WhisperArgs("m.bin", "a.wav", "base", new WhisperOptions(Language: "en"));
        AssertPairInOrder(args, "-m", "m.bin");
        AssertPairInOrder(args, "-f", "a.wav");
        AssertPairInOrder(args, "-of", "base");
        AssertPairInOrder(args, "-l", "en");
        Assert.Contains("-oj", args);
        Assert.Contains("-pp", args);
        Assert.DoesNotContain("-tr", args);   // no translate by default
        Assert.DoesNotContain("-ml", args);   // segment-level by default
    }

    [Fact]
    public void WhisperArgs_adds_translate_and_word_level_when_requested()
    {
        var args = WhisperTranscriber.WhisperArgs("m.bin", "a.wav", "base",
            new WhisperOptions(Language: "auto", Translate: true, WordLevel: true));
        Assert.Contains("-tr", args);
        AssertPairInOrder(args, "-ml", "1");
        Assert.Contains("-sow", args);
        AssertPairInOrder(args, "-l", "auto");
    }

    [Fact]
    public void WhisperArgs_defaults_blank_language_to_auto()
    {
        var args = WhisperTranscriber.WhisperArgs("m.bin", "a.wav", "base", new WhisperOptions(Language: ""));
        AssertPairInOrder(args, "-l", "auto");
    }

    // --- locator ------------------------------------------------------------------------------------------

    [Fact]
    public void Locate_returns_an_existing_override_and_ignores_a_missing_one()
    {
        var prior = Environment.GetEnvironmentVariable(Whisper.OverrideEnvVar);
        var real = Path.Combine(Path.GetTempPath(), "shrike-whisper-cli-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(real, "stub");
            Environment.SetEnvironmentVariable(Whisper.OverrideEnvVar, real);
            Whisper.ResetCache();
            Assert.Equal(real, Whisper.Locate());

            // A missing override must never be returned (it falls through to the other candidates).
            var missing = real + ".gone";
            Environment.SetEnvironmentVariable(Whisper.OverrideEnvVar, missing);
            Whisper.ResetCache();
            Assert.NotEqual(missing, Whisper.Locate());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Whisper.OverrideEnvVar, prior);
            Whisper.ResetCache();
            if (File.Exists(real)) File.Delete(real);
        }
    }

    private static void AssertPairInOrder(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (args[i] == flag && args[i + 1] == value) return;
        Assert.Fail($"Expected '{flag} {value}' in: {string.Join(' ', args)}");
    }
}
