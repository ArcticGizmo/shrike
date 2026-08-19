using System.Buffers.Binary;

namespace Shrike.Core.Audio;

/// <summary>
/// Splices a freshly-recorded take into a voiceover sidecar's PCM — the core of punch-in re-record. The span
/// <c>[startMs, endMs)</c> of the original is replaced by the insert (truncated or silence-padded to fit
/// exactly, so the clip's length and every downstream position are unchanged), with a short linear fade at the
/// insert's edges to soften the seams. Pure 16-bit PCM maths, so it's unit-tested; the editor reads the
/// sidecar, splices, and writes it back (keeping a backup for undo).
/// </summary>
public static class AudioSplice
{
    /// <summary>Return <paramref name="original"/> with <c>[startMs,endMs)</c> replaced by <paramref name="insert"/>.
    /// Total length is preserved. Non-16-bit input, or an empty span, returns the original unchanged.</summary>
    public static byte[] Replace(byte[] original, AudioFormat format, long startMs, long endMs, byte[] insert,
        int fadeMs = 5)
    {
        if (!format.IsPcm16) return original;

        var startByte = (int)Math.Clamp(format.MsToBytes(startMs), 0, original.Length);
        var endByte = (int)Math.Clamp(format.MsToBytes(endMs), startByte, original.Length);
        var spanLen = endByte - startByte;
        if (spanLen <= 0) return original;

        // Fit the insert to exactly the span: truncate if longer, silence-pad if shorter.
        var fitted = new byte[spanLen];
        Array.Copy(insert, 0, fitted, 0, Math.Min(insert.Length, spanLen));

        ApplyFade(fitted, format, fadeMs, fadeIn: true);
        ApplyFade(fitted, format, fadeMs, fadeIn: false);

        var result = new byte[original.Length];
        Array.Copy(original, 0, result, 0, startByte);
        Array.Copy(fitted, 0, result, startByte, spanLen);
        Array.Copy(original, endByte, result, endByte, original.Length - endByte);
        return result;
    }

    // Ramp the first (fadeIn) or last (fadeOut) fadeMs of a 16-bit PCM buffer linearly to/from silence.
    private static void ApplyFade(byte[] pcm, AudioFormat format, int fadeMs, bool fadeIn)
    {
        var fadeSamples = (int)Math.Min(format.MsToBytes(fadeMs), pcm.Length) / 2;
        for (var i = 0; i < fadeSamples; i++)
        {
            var gain = (double)i / fadeSamples; // 0 at the very edge, 1 by the end of the fade
            var pos = fadeIn ? i * 2 : pcm.Length - (i + 1) * 2;
            var s = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(pos, 2));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(pos, 2), (short)(s * gain));
        }
    }
}
