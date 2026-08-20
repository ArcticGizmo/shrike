using System.Net;
using System.Security.Cryptography;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class WhisperModelStoreTests
{
    // --- catalog ------------------------------------------------------------------------------------------

    [Fact]
    public void Catalog_has_the_default_and_finds_by_id()
    {
        Assert.NotNull(WhisperModelCatalog.Find(WhisperModelCatalog.DefaultId));
        Assert.Equal(WhisperModelCatalog.DefaultId, WhisperModelCatalog.Default.Id);
        Assert.Null(WhisperModelCatalog.Find("does-not-exist"));
        Assert.Null(WhisperModelCatalog.Find(null));
        Assert.Contains(WhisperModelCatalog.Models, m => m.Language == "Multilingual"); // non-English covered
    }

    [Fact]
    public void ApproxSize_reads_in_mb_and_gb()
    {
        Assert.Equal("142 MB", new WhisperModel("x", "X", "English", 142L * 1024 * 1024, "x.bin", "u").ApproxSize);
        Assert.Equal("1.5 GB", new WhisperModel("y", "Y", "English", 1536L * 1024 * 1024, "y.bin", "u").ApproxSize);
    }

    // --- installed-state ----------------------------------------------------------------------------------

    [Fact]
    public void IsInstalled_needs_a_real_sized_file()
    {
        using var dir = new TempDir();
        var store = new WhisperModelStore(dir.Path);
        var model = WhisperModelCatalog.Default;

        Assert.False(store.IsInstalled(model));                 // nothing there
        File.WriteAllBytes(store.PathFor(model), new byte[512]); // a truncated stub doesn't count
        Assert.False(store.IsInstalled(model));

        File.WriteAllBytes(store.PathFor(model), new byte[2 * 1024 * 1024]); // a real model is >> 1 MB
        Assert.True(store.IsInstalled(model));
        Assert.Equal(store.PathFor(model), store.InstalledPath(model.Id));
        Assert.Contains(model, store.Installed());
        Assert.Null(store.InstalledPath("does-not-exist"));
    }

    // --- download + verify (network faked) ----------------------------------------------------------------

    [Fact]
    public async Task DownloadAsync_streams_verifies_and_places_the_file()
    {
        var payload = RandomBytes(20_000);
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        using var dir = new TempDir();
        var http = new HttpClient(new FakeHandler(payload));
        var store = new WhisperModelStore(dir.Path, http);
        var model = new WhisperModel("t", "T", "English", payload.Length, "ggml-t.bin", "https://x/ggml-t.bin", sha);

        var reports = new List<double>();
        var path = await store.DownloadAsync(model, new Progress<double>(reports.Add));

        Assert.True(File.Exists(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".part"));               // temp cleaned up
        // (Progress<T> posts asynchronously, so we don't assert the interim values here — DownloadAsync
        //  reports 1.0 last, verified by completion + the file being in place.)
    }

    [Fact]
    public async Task DownloadAsync_rejects_a_checksum_mismatch_and_leaves_nothing()
    {
        var payload = RandomBytes(8_000);
        using var dir = new TempDir();
        var http = new HttpClient(new FakeHandler(payload));
        var store = new WhisperModelStore(dir.Path, http);
        var model = new WhisperModel("t", "T", "English", payload.Length, "ggml-t.bin", "https://x/ggml-t.bin",
            Sha256: "deadbeef"); // wrong on purpose

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DownloadAsync(model));
        Assert.False(File.Exists(store.PathFor(model)));          // not placed
        Assert.False(File.Exists(store.PathFor(model) + ".part")); // and the partial is gone
    }

    [Fact]
    public async Task DownloadAsync_throws_on_http_error()
    {
        using var dir = new TempDir();
        var http = new HttpClient(new FakeHandler(Array.Empty<byte>(), HttpStatusCode.NotFound));
        var store = new WhisperModelStore(dir.Path, http);
        var model = WhisperModelCatalog.Default;

        await Assert.ThrowsAsync<HttpRequestException>(() => store.DownloadAsync(model));
        Assert.False(File.Exists(store.PathFor(model)));
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)((i * 37 + 11) & 0xFF); // deterministic, not Random (banned in some ctx)
        return b;
    }

    private sealed class FakeHandler(byte[] body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentLength = body.Length;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shrike-models-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
