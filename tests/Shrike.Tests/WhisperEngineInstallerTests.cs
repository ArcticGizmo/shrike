using System.IO.Compression;
using System.Net;
using Shrike.Core.Recording;

namespace Shrike.Tests;

public class WhisperEngineInstallerTests
{
    [Fact]
    public async Task DownloadAsync_extracts_the_cli_and_its_dlls_flattened()
    {
        var zip = BuildZip(
            ("bin/Release/whisper-cli.exe", [1, 2, 3]),
            ("bin/Release/ggml.dll", [4, 5, 6]),
            ("bin/Release/notes.txt", [7]),      // not a dll/cli → skipped
            ("other/stray.dll", [8]));            // different folder than the cli → skipped

        using var dir = new TempDir();
        var http = new HttpClient(new FakeHandler(zip));
        var installer = new WhisperEngineInstaller(dir.Path, http);

        var cliPath = await installer.DownloadAsync();

        Assert.Equal(Path.Combine(dir.Path, "whisper-cli.exe"), cliPath);
        Assert.True(File.Exists(Path.Combine(dir.Path, "whisper-cli.exe")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "ggml.dll")));        // sibling DLL came along
        Assert.False(File.Exists(Path.Combine(dir.Path, "notes.txt")));      // non-dll skipped
        Assert.False(File.Exists(Path.Combine(dir.Path, "stray.dll")));      // other folder skipped
    }

    [Fact]
    public async Task DownloadAsync_throws_when_the_archive_has_no_cli()
    {
        var zip = BuildZip(("bin/ggml.dll", [1]));
        using var dir = new TempDir();
        var installer = new WhisperEngineInstaller(dir.Path, new HttpClient(new FakeHandler(zip)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.DownloadAsync());
    }

    [Fact]
    public async Task DownloadAsync_throws_on_http_error()
    {
        using var dir = new TempDir();
        var installer = new WhisperEngineInstaller(dir.Path,
            new HttpClient(new FakeHandler([], HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<HttpRequestException>(() => installer.DownloadAsync());
    }

    private static byte[] BuildZip(params (string Path, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in entries)
            {
                var e = za.CreateEntry(path);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }
        return ms.ToArray();
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
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shrike-engine-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
