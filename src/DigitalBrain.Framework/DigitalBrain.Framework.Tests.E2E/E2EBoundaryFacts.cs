namespace DigitalBrain.Framework.Tests.E2E;

public sealed class E2EBoundaryFacts
{
    [Fact]
    public void BrowserDefaultsResolveFromExplicitChoiceThenEnvironment()
    {
        var resolved = BrowserOptionsResolver.Resolve(new() { Headless = false }, false, false);
        Assert.False(resolved.Headless);
        Assert.Equal(250, resolved.SlowMoMilliseconds);
        Assert.True(BrowserOptionsResolver.Resolve(new(), false, false).Headless);
        Assert.False(BrowserOptionsResolver.Resolve(new(), true, false).Headless);
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserOptionsResolver.Resolve(new() { SlowMoMilliseconds = -1 }, false, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserOptionsResolver.Resolve(new() { StartupTimeout = TimeSpan.Zero }, false, false));
    }

    [Fact]
    public void AHeadlessRunUsesTheContainerBrowserAndAPersonDebuggingGetsALocalOne()
    {
        Assert.Equal(BrowserLocation.Container, BrowserOptionsResolver.Resolve(new() { Headless = true }, false, false).Location);
        Assert.Equal(BrowserLocation.Local, BrowserOptionsResolver.Resolve(new() { Headless = true }, false, true).Location);
        Assert.Equal(BrowserLocation.Local, BrowserOptionsResolver.Resolve(new() { Headless = false }, false, false).Location);
        Assert.Equal(BrowserLocation.Local, BrowserOptionsResolver.Resolve(new() { Headless = true }, false, false, "local").Location);
        Assert.Equal(BrowserLocation.Container, BrowserOptionsResolver.Resolve(new() { Headless = true, Location = BrowserLocation.Container }, false, true, "local").Location);
        Assert.Throws<ArgumentException>(() => BrowserOptionsResolver.Resolve(new() { Headless = true }, false, false, "cloud"));
        Assert.Throws<InvalidOperationException>(() => BrowserOptionsResolver.Resolve(new() { Headless = false, Location = BrowserLocation.Container }, false, false));
    }
}
