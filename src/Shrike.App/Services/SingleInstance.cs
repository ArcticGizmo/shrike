using System.IO.Pipes;
using System.Text;
using Shrike.Core;
using Shrike.Core.Ipc;

namespace Shrike.App.Services;

/// <summary>
/// Enforces one resident Shrike per user session. The primary process holds a named mutex and runs a
/// pipe server; any later launch connects, forwards its <see cref="CaptureAction"/> and exits, so a
/// hotkey wrapper or shortcut re-signals the warm instance instead of paying a cold start.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    // A dev build gets a ".Dev" suffix so it doesn't no-op against the installed release's mutex
    // (see AppProfile) — the two run side-by-side, each primary in its own namespace.
    private static readonly string MutexName =
        @"Local\Shrike.SingleInstance.v1" + (AppProfile.IsDev ? ".Dev" : "");

    private readonly Mutex? _mutex;
    private CancellationTokenSource? _serverCts;
    private bool _disposed;

    public bool IsPrimary { get; }

    private SingleInstance(Mutex? mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    /// <summary>Try to become the primary instance. Non-primary callers should forward and exit.</summary>
    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
            return new SingleInstance(mutex, isPrimary: true);

        mutex.Dispose();
        return new SingleInstance(null, isPrimary: false);
    }

    /// <summary>Fire-and-forget an action to the resident instance. Silently no-ops if it isn't ready.</summary>
    public static void SendToPrimary(CaptureAction action)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", IpcProtocol.PipeName, PipeDirection.Out);
            client.Connect(timeout: 800);
            var payload = Encoding.UTF8.GetBytes(IpcProtocol.Format(action));
            client.Write(payload, 0, payload.Length);
            client.Flush();
        }
        catch
        {
            // Primary not up yet / race on shutdown — nothing useful to do from a transient launcher.
        }
    }

    /// <summary>
    /// Start accepting forwarded actions. <paramref name="onAction"/> is invoked on a thread-pool
    /// thread — the caller marshals to the UI thread.
    /// </summary>
    public void StartServer(Action<CaptureAction> onAction)
    {
        if (!IsPrimary) return;
        _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => ServerLoopAsync(onAction, _serverCts.Token));
    }

    private static async Task ServerLoopAsync(Action<CaptureAction> onAction, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    IpcProtocol.PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

                if (IpcProtocol.TryParse(line, out var action))
                    onAction(action);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // A malformed/aborted connection shouldn't kill the server — keep listening.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _serverCts?.Cancel();
        _serverCts?.Dispose();

        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* not owned / already released */ }
            _mutex.Dispose();
        }
    }
}
