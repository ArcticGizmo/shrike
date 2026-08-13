using System.Runtime.Versioning;
using Shrike.Core.Capture;
using Shrike.Core.Imaging;

namespace Shrike.App.Native;

/// <summary>
/// Copies a <see cref="CapturedImage"/> to the clipboard as PNG + CF_DIBV5 in one call — the shared
/// path for the editor's Copy button and the recent-ring "copy again" actions. Pass
/// <see cref="IntPtr.Zero"/> as the owner when no window is available (e.g. the tray flyout).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CaptureClipboard
{
    public static bool Copy(IntPtr ownerHwnd, CapturedImage image)
    {
        var png = ImageCodec.Encode(image, ImageFormatKind.Png);
        var dib = ImageCodec.ToDibV5(image);
        return ClipboardImage.Set(ownerHwnd, png, dib);
    }
}
