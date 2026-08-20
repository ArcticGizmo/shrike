using System.IO.Compression;
using System.Security.Cryptography;

namespace Shrike.Core.Recording;

/// <summary>The pinned whisper.cpp Windows engine build the app can fetch on demand. Kept in one place so the
/// in-app installer and <c>tools/fetch-whisper.ps1</c> reference the same release.</summary>
public static class WhisperEngine
{
    // ggml-org/whisper.cpp ships prebuilt Windows binaries as whisper-bin-x64.zip (a plain CPU x64 build).
    public const string Version = "v1.9.2";
    public const string Url = "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.2/whisper-bin-x64.zip";
    public const long ApproxBytes = 10L * 1024 * 1024; // ~ download size (zip ~8 MB), for the UI prompt
    // NOTE(release): pin by downloading once and recording the zip's SHA-256 (blank = no verification).
    public const string Sha256 = "";

    public static string ApproxSize => $"{ApproxBytes / (1024.0 * 1024):0} MB";
}

/// <summary>
/// Downloads and installs the whisper.cpp engine binary (and the DLLs it needs) into the Shrike-managed
/// whisper folder — the same folder <see cref="Whisper.Locate"/> probes — so transcription can be enabled
/// from within the app rather than only via a bundled/release build. The transcription <b>model</b> is a
/// separate download (see <see cref="WhisperModelStore"/>). Network access goes through an injectable
/// <see cref="HttpClient"/> so the download→extract path is unit-testable with a fake handler. UI-free.
/// </summary>
public sealed class WhisperEngineInstaller
{
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _dir;
    private readonly HttpClient _http;

    public WhisperEngineInstaller(string? dir = null, HttpClient? http = null)
    {
        _dir = dir ?? AppStorage.WhisperDirectory();
        _http = http ?? Shared;
    }

    /// <summary>True when a whisper engine binary is already resolvable (bundled, managed, or on PATH).</summary>
    public bool IsInstalled => Whisper.IsAvailable;

    /// <summary>
    /// Fetch the pinned engine zip, verify its SHA-256 (when pinned), and extract the CLI plus its sibling DLLs
    /// into the managed folder. Reports 0..1 progress (download dominates). Returns the installed CLI path.
    /// Throws on HTTP error, checksum mismatch, a zip without the CLI, or cancellation.
    /// </summary>
    public async Task<string> DownloadAsync(IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(_dir);
        var tmpZip = Path.Combine(Path.GetTempPath(), "shrike-whisper-" + Guid.NewGuid().ToString("N")[..12] + ".zip");

        try
        {
            using (var resp = await _http.GetAsync(WhisperEngine.Url, HttpCompletionOption.ResponseHeadersRead, cancel)
                       .ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? WhisperEngine.ApproxBytes;
                await using var src = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using var dst = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report(Math.Clamp((double)read / total * 0.95, 0, 0.95));
                }
            }

            if (!string.IsNullOrWhiteSpace(WhisperEngine.Sha256))
            {
                var got = await Sha256HexAsync(tmpZip, cancel).ConfigureAwait(false);
                if (!got.Equals(WhisperEngine.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Downloaded transcription engine failed its checksum — refusing to use it.");
            }

            var cliPath = Extract(tmpZip);
            Whisper.ResetCache(); // a freshly installed engine should be found immediately
            progress?.Report(1.0);
            return cliPath;
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { /* best effort */ }
        }
    }

    // Extract the CLI (whisper-cli.exe, or the legacy main.exe) and every DLL sitting beside it in the zip,
    // flattened into the managed folder so the engine is self-contained next to the exe.
    private string Extract(string zipPath)
    {
        using var za = ZipFile.OpenRead(zipPath);
        var cli = za.Entries.FirstOrDefault(e =>
            NameIs(e, "whisper-cli.exe") || NameIs(e, "whisper.exe") || NameIs(e, "main.exe"))
            ?? throw new InvalidOperationException("The downloaded archive did not contain a whisper CLI.");

        var prefix = DirPrefix(cli.FullName);
        foreach (var e in za.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue;                 // directory entry
            if (DirPrefix(e.FullName) != prefix) continue;              // only files beside the CLI
            if (e != cli && !e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            e.ExtractToFile(Path.Combine(_dir, e.Name), overwrite: true);
        }

        var target = Path.Combine(_dir, cli.Name);
        if (!File.Exists(target)) throw new InvalidOperationException("Failed to place the whisper CLI.");
        return target;
    }

    private static bool NameIs(ZipArchiveEntry e, string file) =>
        string.Equals(e.Name, file, StringComparison.OrdinalIgnoreCase);

    private static string DirPrefix(string fullName)
    {
        var i = fullName.Replace('\\', '/').LastIndexOf('/');
        return i < 0 ? "" : fullName.Replace('\\', '/')[..i];
    }

    private static async Task<string> Sha256HexAsync(string path, CancellationToken cancel)
    {
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, cancel).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
