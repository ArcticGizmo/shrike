using System.Security.Cryptography;

namespace Shrike.Core.Recording;

/// <summary>
/// Manages the on-disk set of downloaded whisper transcription models under
/// <see cref="AppStorage.WhisperModelsDirectory"/>. Downloads a model (streamed, with progress), verifies its
/// SHA-256 when the catalog pins one, and reports which are installed. Network access goes through an
/// injectable <see cref="HttpClient"/> so the download→verify→place path is unit-testable with a fake handler.
/// UI-free.
/// </summary>
public sealed class WhisperModelStore
{
    private static readonly HttpClient Shared = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly string _modelsDir;
    private readonly HttpClient _http;

    public WhisperModelStore(string? modelsDir = null, HttpClient? http = null)
    {
        _modelsDir = modelsDir ?? AppStorage.WhisperModelsDirectory();
        _http = http ?? Shared;
    }

    /// <summary>The path a model's file would live at (whether or not it's downloaded yet).</summary>
    public string PathFor(WhisperModel model) => Path.Combine(_modelsDir, model.FileName);

    /// <summary>True when the model file is present and non-trivial in size (a truncated part-file doesn't count).</summary>
    public bool IsInstalled(WhisperModel model)
    {
        var path = PathFor(model);
        return File.Exists(path) && new FileInfo(path).Length > 1024 * 1024; // any real model is >> 1 MB
    }

    /// <summary>The catalog models currently on disk.</summary>
    public IReadOnlyList<WhisperModel> Installed() =>
        WhisperModelCatalog.Models.Where(IsInstalled).ToList();

    /// <summary>The path of an installed model by id, or null if that id isn't installed.</summary>
    public string? InstalledPath(string? id)
    {
        var model = WhisperModelCatalog.Find(id);
        return model is not null && IsInstalled(model) ? PathFor(model) : null;
    }

    /// <summary>
    /// Download <paramref name="model"/> into the models folder, reporting 0..1 progress, verifying the SHA-256
    /// if the catalog pins one, and moving it into place atomically (via a <c>.part</c> file). Returns the final
    /// path. Throws on an HTTP error, a checksum mismatch, or cancellation (cleaning up the partial file).
    /// </summary>
    public async Task<string> DownloadAsync(WhisperModel model, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        Directory.CreateDirectory(_modelsDir);
        var finalPath = PathFor(model);
        var partPath = finalPath + ".part";

        try
        {
            using (var resp = await _http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead, cancel)
                       .ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? model.ApproxBytes;

                await using var src = await resp.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report(Math.Clamp((double)read / total, 0, 1));
                }
            }

            if (!string.IsNullOrWhiteSpace(model.Sha256))
            {
                var got = await Sha256HexAsync(partPath, cancel).ConfigureAwait(false);
                if (!got.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Downloaded model '{model.Id}' failed its checksum — refusing to use it.");
            }

            File.Move(partPath, finalPath, overwrite: true);
            progress?.Report(1.0);
            return finalPath;
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    /// <summary>Delete an installed model (frees the disk). No-op if it isn't there.</summary>
    public void Delete(WhisperModel model) => TryDelete(PathFor(model));

    private static async Task<string> Sha256HexAsync(string path, CancellationToken cancel)
    {
        await using var s = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(s, cancel).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
