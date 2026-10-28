namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class AppDeclarationFacts
{
    [Fact]
    public void AManifestCannotDeclareTheRemovedEntry()
    {
        Assert.Throws<InvalidDataException>(() => AppDeclaration.Read("""
            static class App
            {
                public const string Name = "report";
                public const string Version = "1.0.0";
                public static readonly AppManifest Manifest = new("Report", "", [], [], [], Entry: new("text", "answer"));
            }
            """));
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
            using IntoChat.OS.Modules.Apps;

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
            using IntoChat.OS.Modules.Apps;
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

    // Phase F: Requires names the semantic types, connector kinds and secrets the app needs, read from syntax and
    // written back the same; Files and Memory declare theirs.
    [Fact]
    public void RequiresReadsBackAndTheShippedFilesAndMemoryDeclareTheirs()
    {
        var requiring = Player with { Requires = new(["person.email"], ["postgres"], ["openai-key"]) };
        var read = AppDeclaration.Read(AppDeclaration.Write(new("player", "1.2.3", requiring, ""))).Manifest.Requires!;
        Assert.Equal(["person.email"], read.SemanticTypes!);
        Assert.Equal(["postgres"], read.Connectors!);
        Assert.Equal(["openai-key"], read.Secrets!);

        var files = FilesRequests.Bundle.Manifest.Requires!;
        // 05-10: Files asks for the storage capability and names no module.
        Assert.Equal(["storage@1"], files.Capabilities!);
        Assert.Null(files.Connectors);
        var memory = AppDeclaration.Read(File.ReadAllText(Path.Combine(Repo(), "features", "os", "Memory", "app.cs"))).Manifest.Requires!;
        Assert.Equal(["postgres"], memory.Connectors!);
    }

    [Fact]
    public void RequiresIsValidated()
    {
        Assert.Throws<InvalidDataException>(() => (Player with { Requires = new(["person.email", "person.email"], ["postgres"]) }).Validated());
        Assert.Throws<InvalidDataException>(() => (Player with { Requires = new(["not an id"], ["postgres"]) }).Validated());
        Assert.Throws<InvalidDataException>(() => (Player with { Requires = new(Secrets: [""], Connectors: ["postgres"]) }).Validated());
        // A slot binds a connector kind the block must list.
        Assert.Throws<InvalidDataException>(() => (Player with { Requires = new(Connectors: ["gmail"]) }).Validated());
        Assert.NotNull((Player with { Requires = new(Connectors: ["postgres"]) }).Validated());
        Assert.Null(Player.Validated().Requires);
    }

    [Fact]
    public void CapabilityIdsAreValidatedByTheirOwnGrammar()
    {
        Assert.NotNull(new AppRequirements(Capabilities: ["storage@1"]).Validated());
        foreach (var bad in new[] { "storage@x", "Storage@1", "storage", "storage@0", "" })
        {
            var error = Assert.Throws<InvalidDataException>(() => new AppRequirements(Capabilities: [bad]).Validated());
            Assert.Contains($"'{bad}'", error.Message, StringComparison.Ordinal);
        }
        Assert.Throws<InvalidDataException>(() => new AppRequirements(Capabilities: ["storage@1", "storage@1"]).Validated());
    }

    [Fact]
    public void ACapabilitySlotMustBeRequired()
    {
        AppManifest browser = new("Browser", "", [], [], [new("drive", Capability: "storage@1")]);
        var missing = Assert.Throws<InvalidDataException>(browser.Validated);
        Assert.Contains("storage@1", missing.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => (browser with { Requires = new(Capabilities: ["secrets@1"]) }).Validated());
        Assert.NotNull((browser with { Requires = new(Capabilities: ["storage@1"]) }).Validated());
        Assert.Throws<InvalidDataException>(() => new AppManifest("Both", "", [], [], [new("x", "postgres", "storage@1")],
            Requires: new(Connectors: ["postgres"], Capabilities: ["storage@1"])).Validated());
    }

    [Fact]
    public void CapabilitiesAndCapabilitySlotsReadBackIdentically()
    {
        AppManifest browser = new("Browser", "Browses.", [new("open", "Open")], [],
            [new("drive", Capability: "storage@1"), new("tables", "postgres")],
            Requires: new(Connectors: ["postgres"], Capabilities: ["storage@1", "secrets@1"]));
        var written = AppDeclaration.Write(new("browser", "1.0.0", browser, ""));
        Assert.Contains("Capability: \"storage@1\"", written, StringComparison.Ordinal);
        var read = AppDeclaration.Read(written).Manifest;
        Assert.Equal(browser.Slots, read.Slots);
        Assert.Equal(browser.Requires, read.Requires);
        Assert.Equal(["storage@1", "secrets@1"], read.Requires!.Capabilities!);
        Assert.Equal(written, AppDeclaration.Write(new("browser", "1.0.0", read, "")));
    }

    [Fact]
    public void TheFilesManifestNamesNoModule()
    {
        var source = File.ReadAllText(Path.Combine(Repo(), "src", "IntoChat.OS.Modules", "IntoChat.OS", "Apps", "src", "IntoChat.OS.Modules.Apps", "Files", "Files.cs"));
        Assert.DoesNotContain("windows", source, StringComparison.Ordinal);
        Assert.DoesNotContain("google-drive", source, StringComparison.Ordinal);
        Assert.Contains("storage@1", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ConsentCoversCapabilitiesWithoutChangingOlderDigests()
    {
        Assert.NotEqual(AppConsent.Of(new()), AppConsent.Of(new(Capabilities: ["storage@1"])));
        Assert.Equal(AppConsent.Of(new()), AppConsent.Of(new(Capabilities: [])));
        Assert.False(AppConsent.IsEmpty(new(Capabilities: ["storage@1"])));
        Assert.Equal(["storage@1"], AppConsent.Added(new(), new(Capabilities: ["storage@1"])).Capabilities!);
    }

    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Intochat.OS.slnx"))) { return dir.FullName; }
        }
        throw new DirectoryNotFoundException("Intochat.OS.slnx was not found above the test output.");
    }
}
