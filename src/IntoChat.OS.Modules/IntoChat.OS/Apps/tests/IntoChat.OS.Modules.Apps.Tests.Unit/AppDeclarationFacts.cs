namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class AppDeclarationFacts
{
    private const string Source = """
        /* A calculator.
           Adds numbers. */
        using IntoChat.OS.Modules.Apps;

        static class App
        {
            public const string Name = "calculator";
            public const string Version = "1.1.0";

            public static readonly AppManifest Manifest = new(
                Title: "Calculator",
                Description: "Adds " + "numbers",
                Operations: [new("add", "Adds two numbers")],
                Settings: [new("precision", "Digits", "2")],
                Slots: [new("sheet", "google-sheets")],
                Icon: "calculate",
                Toolbar: [new("add", Kind: AppToolbarKinds.Slider, Min: -1.5, Max: 10)]);
        }
        """;

    [Fact]
    public void ADeclarationIsReadFromSyntaxIntoNameVersionManifestAndSpec()
    {
        var content = AppDeclaration.Read(Source);

        Assert.Equal(("calculator", "1.1.0"), (content.Name, content.Version));
        Assert.Equal("Calculator", content.Manifest.Title);
        Assert.Equal("Adds numbers", content.Manifest.Description);
        Assert.Equal("add", Assert.Single(content.Manifest.Operations).Name);
        Assert.Equal(("precision", "2"), (content.Manifest.Settings[0].Name, content.Manifest.Settings[0].Default));
        Assert.Equal("google-sheets", Assert.Single(content.Manifest.Slots).Connector);
        var entry = Assert.Single(content.Manifest.Toolbar!);
        Assert.Equal((AppToolbarKinds.Slider, -1.5, 10d), (entry.Kind, entry.Min, entry.Max));
        Assert.Contains("Adds numbers.", content.Spec, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingThenReadingGivesTheSameDeclaration()
    {
        var original = AppDeclaration.Read(Source);

        var again = AppDeclaration.Read(AppDeclaration.Write(original));

        Assert.Equal(original.Name, again.Name);
        Assert.Equal(original.Version, again.Version);
        Assert.Equal(original.Spec, again.Spec);
        Assert.Equal(original.Manifest.Title, again.Manifest.Title);
        Assert.Equal(original.Manifest.Operations, again.Manifest.Operations);
        Assert.Equal(original.Manifest.Toolbar, again.Manifest.Toolbar);
    }

    [Fact]
    public void ASpecContainingACommentTerminatorIsWrittenAsLineComments()
    {
        var content = new AppDeclarationContent("a", "1", new("T", "D", [], [], []), "ends */ here");

        var text = AppDeclaration.Write(content);

        Assert.Contains("// ends */ here", text, StringComparison.Ordinal);
        Assert.Equal("ends */ here", AppDeclaration.Read(text).Spec);
    }

    [Theory]
    [InlineData("static class Other { }")]
    [InlineData("static class App { public const string Version = \"1\"; }")]
    [InlineData("static class App { public const string Name = \"a\"; public const string Version = \"1\"; }")]
    public void ASourceWithoutTheDeclaredMembersIsRefused(string source)
        => Assert.Throws<InvalidDataException>(() => AppDeclaration.Read(source));

    [Fact]
    public void AnExpressionThatWouldHaveToRunIsRefused()
    {
        const string source = """
            using IntoChat.OS.Modules.Apps;
            static class App
            {
                public const string Name = "a";
                public const string Version = "1";
                public static readonly AppManifest Manifest = new(Title: System.Environment.MachineName, Description: "d", Operations: [], Settings: [], Slots: []);
            }
            """;

        var error = Assert.Throws<InvalidDataException>(() => AppDeclaration.Read(source));

        Assert.StartsWith(AppFiles.DeclarationPath + ":", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolbarEntryForAnUndeclaredOperationIsRefused()
    {
        var source = Source.Replace("new(\"add\", Kind", "new(\"subtract\", Kind", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => AppDeclaration.Read(source));
    }

    [Fact]
    public void ASyntaxErrorIsReportedWithItsLine()
        => Assert.Contains(AppFiles.DeclarationPath, Assert.Throws<InvalidDataException>(() => AppDeclaration.Read("static class App {")).Message, StringComparison.Ordinal);
}
