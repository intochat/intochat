namespace IntoChat.Cloud.Localization.Tests;

public sealed class LocalizationFacts
{
    [Theory]
    [InlineData("cloud", true)]
    [InlineData("local", false)]
    [InlineData(null, false)]
    public void OnlyCloudLoadsLocalization(string? topology, bool loads) => Assert.Equal(loads, LocalizationModule.LoadsUnder(topology));

    [Fact]
    public void EnglishAndRussianTablesResolveTheLoginHeading()
    {
        var localizer = new TableLocalizer();
        Assert.Equal("Log in to IntoChat", localizer.Get("en", "Lobby.Login.Heading"));
        Assert.Equal("Вход в IntoChat", localizer.Get("ru", "Lobby.Login.Heading"));
        Assert.Equal("Log in to IntoChat", localizer.Get("de", "Lobby.Login.Heading"));
        Assert.Equal("missing.key", localizer.Get("en", "missing.key"));
        Assert.Contains("en", localizer.Languages);
        Assert.Contains("ru", localizer.Languages);
    }
}
