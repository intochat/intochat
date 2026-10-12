using DigitalBrain.Apps;

namespace DigitalBrain.Modules.Apps.Tests;

public sealed class AppDeclarationFacts
{
    [Fact]
    public void ADeclaredEntrySurvivesSyntaxAndJsonRoundTrips()
    {
        var declaration = AppDeclaration.Read("""
            static class App
            {
                public const string Name = "report";
                public const string Version = "1.0.0";
                public static readonly AppManifest Manifest = new("Report", "", [], [], [], Entry: new("text", "documents/answer"));
            }
            """);
        var json = System.Text.Json.JsonSerializer.Serialize(declaration.Manifest);
        Assert.Contains("documents/answer", json, StringComparison.Ordinal);
        Assert.Equal(json, System.Text.Json.JsonSerializer.Serialize(AppDeclaration.Read(AppDeclaration.Write(declaration)).Manifest));
    }

    [Theory]
    [InlineData("text", "../answer")]
    [InlineData("text", "/answer")]
    [InlineData("text", "documents//answer")]
    [InlineData("text", "documents/./answer")]
    [InlineData("text", "https://example.com")]
    [InlineData("text", "answer%2Fother")]
    [InlineData("invented", "answer")]
    public void ADeclaredEntryRejectsUnsafeNamesAndUnsupportedKinds(string kind, string name)
    {
        var source = $$"""
            static class App
            {
                public const string Name = "report";
                public const string Version = "1.0.0";
                public static readonly AppManifest Manifest = new("Report", "", [], [], [], Entry: new("{{kind}}", "{{name}}"));
            }
            """;
        Assert.Throws<InvalidDataException>(() => AppDeclaration.Read(source));
    }

    private static readonly AppManifest Player = new("Player", "Plays \"music\".",
        [new("play", "Play"), new("volume", "Set the volume")],
        [new("Model", "Who answers", "IGemma4")],
        [new("tables", "postgres")],
        "icon.svg",
        [new("play", Icon: "play", Label: "Play"), new("volume", AppToolbarKinds.Slider, "volume", null, 0, 100.5)]);

    [Fact]
    public void AWrittenDeclarationReadsBackIdentically()
    {
        var written = AppDeclaration.Write(new("player", "1.2.3", Player, "# Player\n\nIt plays."));
        var read = AppDeclaration.Read(written);

        Assert.Equal(("player", "1.2.3", "# Player\n\nIt plays."), (read.Name, read.Version, read.Spec));
        Assert.Equal(Player with { Operations = [], Settings = [], Slots = [], Toolbar = null }, read.Manifest with { Operations = [], Settings = [], Slots = [], Toolbar = null });
        Assert.Equal(Player.Operations, read.Manifest.Operations);
        Assert.Equal(Player.Settings, read.Manifest.Settings);
        Assert.Equal(Player.Slots, read.Manifest.Slots);
        Assert.Equal(Player.Toolbar, read.Manifest.Toolbar);
    }

    [Fact]
    public void ASpecHoldingACommentCloserIsWrittenAsLineComments()
    {
        var read = AppDeclaration.Read(AppDeclaration.Write(new("player", "1.0.0", Player, "Ends with */ here.\n\nAnd more.")));
        Assert.Equal("Ends with */ here.\n\nAnd more.", read.Spec);
    }

    [Fact]
    public void AHandWrittenDeclarationWithNamedPositionalAndQualifiedArgumentsReads()
    {
        var read = AppDeclaration.Read("""
            // Echoes what it hears.
            using DigitalBrain.Apps;

            static class App
            {
                public const string Name = "echo";
                public const string Version = "0.1.0";
                public static readonly AppManifest Manifest = new AppManifest(
                    Description: "Echoes " + "back.",
                    Title: "Echo",
                    Operations: [new AppOperation("ask", "Echo")],
                    Settings: new AppSetting[] { },
                    Slots: []);
            }
            """);
        Assert.Equal("Echoes what it hears.", read.Spec);
        Assert.Equal(("Echo", "Echoes back."), (read.Manifest.Title, read.Manifest.Description));
        Assert.Equal("ask", Assert.Single(read.Manifest.Operations).Name);
    }

    [Fact]
    public void ADeclarationThatComputesItsManifestIsRefused()
    {
        var error = Assert.Throws<InvalidDataException>(() => AppDeclaration.Read("""
            using DigitalBrain.Apps;
            static class App
            {
                public const string Name = "echo";
                public const string Version = "0.1.0";
                public static readonly AppManifest Manifest = new(Title(), "d", [], [], []);
                static string Title() => "Echo";
            }
            """));
        Assert.Contains("app.cs:6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileWithoutTheAppClassIsRefused()
        => Assert.Throws<InvalidDataException>(() => AppDeclaration.Read("static class Other { }"));
}
