using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shrike.Core.Recording;

/// <summary>Options for a transcription run — the language (an ISO code like <c>en</c>, or <c>auto</c> to
/// detect), whether to translate to English, and whether to emit tight word-level cues rather than the
/// default sentence-ish segments.</summary>
public sealed record WhisperOptions(string Language = "auto", bool Translate = false, bool WordLevel = false);

/// <summary>Turns a narration audio file into timed <see cref="CaptionCue"/>s. UI-free.</summary>
public interface ITranscriber
{
    /// <summary>Transcribe <paramref name="audioPath"/> using the model at <paramref name="modelPath"/>. The
    /// returned cues are in the <b>audio file's own</b> time (0-based); the caller maps them into the clip's
    /// source time. Throws with a clear message when the engine/model is missing or the run fails.</summary>
    Task<IReadOnlyList<CaptionCue>> TranscribeAsync(
        string audioPath, string modelPath, WhisperOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancel = default);
}

/// <summary>
/// Transcribes narration with a bundled/managed whisper.cpp CLI, entirely offline. The pipeline is:
/// FFmpeg resamples the sidecar to the 16 kHz mono PCM WAV whisper wants, whisper-cli writes a JSON
/// transcript, and <see cref="ParseJson"/> turns its segments into <see cref="CaptionCue"/>s. Shells out to
/// external tools exactly as <see cref="VideoExporter"/> does, so it lives in Core (UI-free) and its pure
/// parts — the ffmpeg/whisper arg builders and the JSON parser — are unit-testable without either binary.
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    private readonly string _ffmpegPath;
    private readonly string _whisperPath;

    public WhisperTranscriber(string ffmpegPath, string whisperPath)
    {
        _ffmpegPath = ffmpegPath;
        _whisperPath = whisperPath;
    }

    /// <summary>Build one using the located tools, or null if either the ffmpeg or whisper binary is missing
    /// (the caller then prompts to install the engine / download a model).</summary>
    public static WhisperTranscriber? TryCreate()
    {
        var ffmpeg = Ffmpeg.Locate();
        var whisper = Whisper.Locate();
        return ffmpeg is not null && whisper is not null ? new WhisperTranscriber(ffmpeg, whisper) : null;
    }

    public async Task<IReadOnlyList<CaptionCue>> TranscribeAsync(
        string audioPath, string modelPath, WhisperOptions? options = null,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        if (!File.Exists(audioPath)) throw new FileNotFoundException("Audio file not found.", audioPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Whisper model not found.", modelPath);

        var opts = options ?? new WhisperOptions();
        var work = Path.Combine(Path.GetTempPath(), "shrike-caption-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        var wav = Path.Combine(work, "audio16k.wav");
        var outBase = Path.Combine(work, "transcript"); // whisper writes <outBase>.json

        try
        {
            // Stage 1 — resample to 16 kHz mono PCM (ffmpeg); coarse 0..10% of the bar.
            progress?.Report(0.0);
            await RunAsync(_ffmpegPath, ResampleArgs(audioPath, wav), null, cancel).ConfigureAwait(false);
            progress?.Report(0.10);

            // Stage 2 — transcribe; whisper prints "progress = NN%" to stderr, scaled into 10..100%.
            await RunAsync(_whisperPath, WhisperArgs(modelPath, wav, outBase, opts),
                pct => progress?.Report(0.10 + 0.90 * Math.Clamp(pct, 0, 1)), cancel).ConfigureAwait(false);

            var jsonPath = outBase + ".json";
            if (!File.Exists(jsonPath))
                throw new InvalidOperationException("Transcription produced no output.");
            var cues = ParseJson(await File.ReadAllTextAsync(jsonPath, cancel).ConfigureAwait(false));
            progress?.Report(1.0);
            return cues;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    // ---- pure, testable builders -------------------------------------------------------------------------

    /// <summary>FFmpeg args to resample any audio to the 16 kHz mono 16-bit PCM WAV whisper.cpp expects.</summary>
    public static IReadOnlyList<string> ResampleArgs(string input, string output) =>
        ["-y", "-hide_banner", "-loglevel", "error", "-i", input,
         "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", output];

    /// <summary>whisper-cli args: model, input WAV, JSON output to <paramref name="outBase"/>.json, language,
    /// optional translate / word-level, with progress prints on stderr.</summary>
    public static IReadOnlyList<string> WhisperArgs(string model, string wav, string outBase, WhisperOptions opts)
    {
        var a = new List<string>
        {
            "-m", model, "-f", wav,
            "-oj", "-of", outBase,     // JSON transcript to <outBase>.json
            "-l", string.IsNullOrWhiteSpace(opts.Language) ? "auto" : opts.Language,
            "-pp",                     // print progress to stderr so we can drive a bar
        };
        if (opts.Translate) a.Add("-tr");
        if (opts.WordLevel) { a.Add("-ml"); a.Add("1"); a.Add("-sow"); } // one word per cue, split on word
        return a;
    }

    /// <summary>Parse a whisper.cpp JSON transcript into cues. Reads each <c>transcription[]</c> segment's
    /// millisecond <c>offsets</c> (falling back to parsing the <c>timestamps</c> strings), trims the text, and
    /// drops empty/zero-length segments. Tolerant of missing fields and malformed JSON (returns none).</summary>
    public static IReadOnlyList<CaptionCue> ParseJson(string json)
    {
        var cues = new List<CaptionCue>();
        if (string.IsNullOrWhiteSpace(json)) return cues;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("transcription", out var segs) ||
                segs.ValueKind != JsonValueKind.Array)
                return cues;

            foreach (var seg in segs.EnumerateArray())
            {
                var text = seg.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Trim() : "";
                if (text.Length == 0) continue;

                long? from = null, to = null;
                if (seg.TryGetProperty("offsets", out var off) && off.ValueKind == JsonValueKind.Object)
                {
                    from = ReadMs(off, "from");
                    to = ReadMs(off, "to");
                }
                if ((from is null || to is null) && seg.TryGetProperty("timestamps", out var ts) &&
                    ts.ValueKind == JsonValueKind.Object)
                {
                    from ??= ParseTimestamp(ts.TryGetProperty("from", out var f) ? f.GetString() : null);
                    to ??= ParseTimestamp(ts.TryGetProperty("to", out var e) ? e.GetString() : null);
                }
                if (from is { } a && to is { } b && b > a)
                    cues.Add(new CaptionCue(a, b, text));
            }
        }
        catch (JsonException)
        {
            return new List<CaptionCue>();
        }
        return cues;
    }

    private static long? ReadMs(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String => ParseTimestamp(v.GetString()),
            _ => null,
        };
    }

    // whisper timestamp strings look like "00:01:02,500" (HH:MM:SS,mmm) or with a '.' separator.
    private static long? ParseTimestamp(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Regex.Match(s.Trim(), @"^(\d+):(\d{2}):(\d{2})[.,](\d{1,3})$");
        if (!m.Success) return null;
        long h = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        long min = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        long sec = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        long ms = long.Parse(m.Groups[4].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        return ((h * 60 + min) * 60 + sec) * 1000 + ms;
    }

    // ---- process runner ----------------------------------------------------------------------------------

    private static readonly Regex ProgressRe = new(@"progress\s*=\s*(\d+)\s*%", RegexOptions.Compiled);

    private static async Task RunAsync(string exe, IReadOnlyList<string> args,
        Action<double>? onProgress, CancellationToken cancel)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{Path.GetFileName(exe)}'.");

        var tail = new StringBuilder();
        var stderrTask = DrainAsync(proc.StandardError, line =>
        {
            tail.AppendLine(line);
            if (tail.Length > 4000) tail.Remove(0, tail.Length - 4000);
            if (onProgress is not null)
            {
                var m = ProgressRe.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value, out var pct)) onProgress(pct / 100.0);
            }
        });
        var stdoutTask = DrainAsync(proc.StandardOutput, _ => { });

        try
        {
            await proc.WaitForExitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            await Safe(stderrTask); await Safe(stdoutTask);
            throw;
        }

        await Safe(stderrTask); await Safe(stdoutTask);
        if (proc.ExitCode != 0)
        {
            var msg = tail.ToString().Trim();
            throw new InvalidOperationException(
                $"{Path.GetFileName(exe)} failed (exit {proc.ExitCode}).{(msg.Length == 0 ? "" : " " + msg)}");
        }
    }

    private static async Task DrainAsync(StreamReader reader, Action<string> onLine)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null) onLine(line);
    }

    private static async Task Safe(Task t) { try { await t.ConfigureAwait(false); } catch { /* ignore */ } }
}
