using DigitalBrain.Caching;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class BoundedCacheFacts
{
    [Fact]
    public void AHitReturnsTheStoredValue()
    {
        var cache = new BoundedCache<string, int>(2);
        cache.Set("a", 1);

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(1, value);
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void AFullCacheEvictsOnlyTheLeastRecentlyUsedEntry()
    {
        var cache = new BoundedCache<string, int>(3);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3);

        cache.Set("d", 4);

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void ReadingAnEntryRefreshesItSoTheNextOldestGoes()
    {
        var cache = new BoundedCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGet("a", out _));

        cache.Set("c", 3);

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void GetOrAddBuildsOnceForAHitAndReplacingAKeyKeepsTheCountSteady()
    {
        var cache = new BoundedCache<string, int>(2);
        var builds = 0;

        Assert.Equal(7, cache.GetOrAdd("a", _ => { builds++; return 7; }));
        Assert.Equal(7, cache.GetOrAdd("a", _ => { builds++; return 8; }));
        cache.Set("a", 9);

        Assert.Equal(1, builds);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(9, value);
    }
}
