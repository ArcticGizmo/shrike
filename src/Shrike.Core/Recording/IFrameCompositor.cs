namespace Shrike.Core.Recording;

/// <summary>
/// A per-frame hook for the composite render pass (SC3). <see cref="Compose"/> may draw onto the frame's
/// BGRA buffer in place; the smooth-cursor pass (SC4) will draw the synthetic cursor and click ripples here.
/// </summary>
public interface IFrameCompositor
{
    /// <summary>Draw onto <paramref name="bgra"/> (top-down BGRA, length <c>width*height*4</c>) in place.</summary>
    /// <param name="frameIndex">Zero-based output frame index on the edited timeline.</param>
    void Compose(byte[] bgra, int width, int height, int frameIndex);
}

/// <summary>
/// A no-op compositor — the render pass reproduces the edited video unchanged. Used to prove the
/// decode → encode round-trip stays in sync before any drawing exists.
/// </summary>
public sealed class IdentityCompositor : IFrameCompositor
{
    public void Compose(byte[] bgra, int width, int height, int frameIndex) { }
}
