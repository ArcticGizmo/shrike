namespace Shrike.Core.Recording;

/// <summary>
/// Applies an ordered list of <see cref="IFrameCompositor"/> effects to each frame, in sequence, sharing one
/// BGRA buffer. This is the backbone of the effects model: each effect is independent and composes onto the
/// result of the ones before it, so new effects are added by appending to the chain rather than editing a
/// monolithic compositor. Order is meaningful — frame <em>transforms</em> (e.g. <see cref="ZoomCompositor"/>)
/// run before <em>overlays</em> (e.g. <see cref="CursorCompositor"/>) so overlays land on the transformed
/// pixels. Itself an <see cref="IFrameCompositor"/>, so it drops straight into <see cref="FrameCompositePipeline"/>.
/// </summary>
public sealed class CompositorChain : IFrameCompositor
{
    private readonly IReadOnlyList<IFrameCompositor> _effects;

    public CompositorChain(params IFrameCompositor[] effects) => _effects = effects;

    public CompositorChain(IReadOnlyList<IFrameCompositor> effects) => _effects = effects;

    public void Compose(byte[] bgra, int width, int height, int frameIndex)
    {
        for (var i = 0; i < _effects.Count; i++)
            _effects[i].Compose(bgra, width, height, frameIndex);
    }
}
