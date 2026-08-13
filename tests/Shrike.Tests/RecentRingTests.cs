using Shrike.Core.Capture;

namespace Shrike.Tests;

public class RecentRingTests
{
    // A solid-colour capture of a given size, tagged with a distinct blue value so we can identify it.
    private static CapturedImage Image(int w, int h, byte tag)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = tag; // B
            bgra[i + 3] = 255; // A
        }
        return new CapturedImage(w, h, bgra, new PixelBounds(0, 0, w, h), DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Add_puts_newest_first()
    {
        var ring = new RecentRing();
        ring.Add(Image(4, 4, 1));
        ring.Add(Image(4, 4, 2));

        Assert.Equal(2, ring.Count);
        Assert.Equal(2, ring.Items[0].Image.Bgra[0]); // most recent at index 0
        Assert.Equal(1, ring.Items[1].Image.Bgra[0]);
    }

    [Fact]
    public void Count_cap_evicts_the_oldest()
    {
        var ring = new RecentRing(maxCount: 3);
        for (byte t = 1; t <= 5; t++)
            ring.Add(Image(4, 4, t));

        Assert.Equal(3, ring.Count);
        // Newest three kept (5,4,3); oldest two (1,2) evicted.
        Assert.Equal(new byte[] { 5, 4, 3 }, ring.Items.Select(i => i.Image.Bgra[0]).ToArray());
    }

    [Fact]
    public void Byte_cap_evicts_until_within_budget()
    {
        // Each image is 100x100x4 = 40,000 bytes. Cap at 100,000 => at most 2 fit.
        var ring = new RecentRing(maxCount: 100, maxBytes: 100_000);
        for (byte t = 1; t <= 5; t++)
            ring.Add(Image(100, 100, t));

        Assert.Equal(2, ring.Count);
        Assert.True(ring.TotalBytes <= ring.MaxBytes);
        Assert.Equal(5, ring.Items[0].Image.Bgra[0]); // newest survives
    }

    [Fact]
    public void Byte_cap_keeps_the_newest_even_when_it_alone_exceeds_the_cap()
    {
        var ring = new RecentRing(maxCount: 100, maxBytes: 1_000);
        ring.Add(Image(100, 100, 7)); // 40,000 bytes, far over the 1,000-byte cap

        Assert.Equal(1, ring.Count);
        Assert.Equal(7, ring.Items[0].Image.Bgra[0]);
    }

    [Fact]
    public void Remove_by_reference_and_id()
    {
        var ring = new RecentRing();
        var a = ring.Add(Image(4, 4, 1));
        var b = ring.Add(Image(4, 4, 2));

        Assert.True(ring.Remove(a));
        Assert.False(ring.Remove(a)); // already gone
        Assert.True(ring.Remove(b.Id));
        Assert.Equal(0, ring.Count);
    }

    [Fact]
    public void Clear_empties_the_ring()
    {
        var ring = new RecentRing();
        ring.Add(Image(4, 4, 1));
        ring.Add(Image(4, 4, 2));
        ring.Clear();

        Assert.Equal(0, ring.Count);
        Assert.Equal(0, ring.TotalBytes);
    }

    [Fact]
    public void Changed_fires_on_mutation_only()
    {
        var ring = new RecentRing();
        var fires = 0;
        ring.Changed += () => fires++;

        var item = ring.Add(Image(4, 4, 1)); // 1
        ring.Remove(item);                    // 2
        ring.Remove(item);                    // no-op, no fire
        ring.Clear();                         // no-op (already empty), no fire

        Assert.Equal(2, fires);
    }

    [Fact]
    public void Add_builds_a_downscaled_thumbnail()
    {
        var ring = new RecentRing(thumbnailSize: 32);
        var entry = ring.Add(Image(256, 128, 9));

        Assert.Equal(32, entry.Thumbnail.Width);   // longest side clamped to 32
        Assert.Equal(16, entry.Thumbnail.Height);  // aspect preserved
        Assert.Equal(256, entry.Image.Width);      // full image untouched
    }
}
