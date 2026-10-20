using IntoChat.OS.Integrations;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;

namespace IntoChat.OS.Apps.Tests.Apps;

// The gallery a brain's scripts read: shipped apps are built-in, the brain owner's own
// publications are theirs, everyone else's are from others; Get installs for real or says why not.
public sealed class AppStoreFacts
{
    private const string Spec = "## Scenario: Echoes\nIt echoes.";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static Task<ITestBrain> Start() => TestBrain.Create()
        .WithModule<AppsModule, AppsOptions>(options => options.Features = Path.Combine(AppContext.BaseDirectory, "FeatureFixture"))
        .WithFakeAppRuntime(new())
        .StartAsync(Ct);

    private static async Task Publish(ITestBrain brain, Person person, string name, string title)
    {
        var app = brain.Get<IApp>(IApp.Key(person.BrainId, name));
        using var _ = person.AsCaller();
        await app.Create(new(title, "echoes", [new("ask", "ask it")], [], []), Spec);
        await app.BeginBuild(null);
        await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["synapses/echo.cs"] = "// v1" }, null);
        await app.Publish();
    }

    private static IAppStore Store(ITestBrain brain, Person person) => brain.Get<IAppStore>(IAppStore.Key(person.BrainId));

    [Trait("host", "own")]
    [Fact]
    public async Task ListGroupsShippedOwnAndForeignApps()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var alice = await brain.PersonAsync("alice", Ct);
        await Publish(brain, intochat, "echo", "Echo");
        await Publish(brain, alice, "notes", "Notes");

        using var _ = alice.AsCaller();
        var entries = await Store(brain, alice).List();

        var greeter = Assert.Single(entries, entry => entry.Name == "greeter");
        Assert.Equal(("builtin", "app", false), (greeter.Group, greeter.Kind, greeter.Installed));
        var notes = Assert.Single(entries, entry => entry.Name == "notes");
        Assert.Equal(("mine", true, "notes"), (notes.Group, notes.Installed, notes.InstalledName));
        var echo = Assert.Single(entries, entry => entry.Name == "echo");
        Assert.Equal(("others", "intochat", "Echo", false), (echo.Group, echo.Publisher, echo.Title, echo.Installed));
        Assert.Null(echo.InstalledName);
        Assert.Null(echo.ConnectorId);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ListShowsAnUnpublishedOwnAppAsMine()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        await brain.Get<IApp>(IApp.Key(alice.BrainId, "draft")).Create(new("Draft", "mine", [new("ask", "ask it")], [], []), Spec);

        var draft = Assert.Single(await Store(brain, alice).List(), entry => entry.Name == "draft");

        Assert.Equal(("mine", "Draft", true, "draft"), (draft.Group, draft.Title, draft.Installed, draft.InstalledName));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task GetInstallsAForeignApp()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var alice = await brain.PersonAsync("alice", Ct);
        await Publish(brain, intochat, "echo", "Echo");

        using var _ = alice.AsCaller();
        var outcome = await Store(brain, alice).Get("intochat", "echo");

        Assert.Equal(new InstallOutcome(true, "echo", null), outcome);
        var echo = Assert.Single(await Store(brain, alice).List(), entry => entry.Name == "echo" && entry.Group == "others");
        Assert.Equal((true, "echo"), (echo.Installed, echo.InstalledName));
        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(IApp.Key(alice.BrainId, "echo")).Read()).Status);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task GetOnYourOwnPublicationSaysWhyAndInstallsNothing()
    {
        await using var brain = await Start();
        var alice = await brain.PersonAsync("alice", Ct);
        await Publish(brain, alice, "notes", "Notes");

        using var _ = alice.AsCaller();
        var outcome = await Store(brain, alice).Get("alice", "notes");

        Assert.False(outcome.Ok);
        Assert.Null(outcome.InstalledName);
        Assert.Contains("already yours", outcome.Message);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task GetOnANameYouAlreadyHaveSaysWhyAndLeavesYourAppUntouched()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var alice = await brain.PersonAsync("alice", Ct);
        await Publish(brain, intochat, "echo", "Echo");
        using var _ = alice.AsCaller();
        var own = brain.Get<IApp>(IApp.Key(alice.BrainId, "echo"));
        await own.Create(new("Alice's echo", "mine", [new("ask", "ask it")], [], []), Spec);

        var outcome = await Store(brain, alice).Get("intochat", "echo");

        Assert.False(outcome.Ok);
        Assert.Contains("already have", outcome.Message);
        var after = await own.Read();
        Assert.Equal("Alice's echo", after.Manifest.Title);
        Assert.Null(after.Origin);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUneditedInstalledForeignAppIsOneEntryFromOthers()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var alice = await brain.PersonAsync("alice", Ct);
        await Publish(brain, intochat, "echo", "Echo");
        using var _ = alice.AsCaller();
        await Store(brain, alice).Get("intochat", "echo");

        var echo = Assert.Single(await Store(brain, alice).List(), entry => entry.Name == "echo");

        Assert.Equal(("others", true, "echo", "Echo"), (echo.Group, echo.Installed, echo.InstalledName, echo.Title));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnEditedCopyIsOnlyMineAndCalledACopy()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var alice = await brain.PersonAsync("alice", Ct);
        await Publish(brain, intochat, "echo", "Echo");
        using var _ = alice.AsCaller();
        await Store(brain, alice).Get("intochat", "echo");
        await brain.Get<IApp>(IApp.Key(alice.BrainId, "echo")).WriteFile("notes.md", "mine now", Guid.NewGuid());

        var echo = Assert.Single(await Store(brain, alice).List(), entry => entry.Name == "echo");

        Assert.Equal(("mine", "Copy of Echo", true, "echo"), (echo.Group, echo.Title, echo.Installed, echo.InstalledName));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AScriptSeesTheSameGroupsAsItsOwner()
    {
        await using var brain = await Start();
        var intochat = await brain.PersonAsync("intochat", Ct);
        var alice = await brain.PersonAsync("alice", Ct);
        await Publish(brain, intochat, "echo", "Echo");
        await Publish(brain, alice, "notes", "Notes");

        using var _ = alice.AsApp(IApp.Key(alice.BrainId, "notes"));
        var entries = await Store(brain, alice).List();

        Assert.Equal("mine", Assert.Single(entries, entry => entry.Name == "notes").Group);
        Assert.Equal("others", Assert.Single(entries, entry => entry.Name == "echo").Group);
        Assert.Equal(new InstallOutcome(true, "echo", null), await Store(brain, alice).Get("intochat", "echo"));
    }

    [Fact]
    public void GmailAndSalesforceLeadTheConnectionsThenTheRestByName()
    {
        ConnectorOffer[] offers = [new("github", "GitHub", true, false), new("salesforce", "Salesforce", false, false), new("asana", "Asana", true, false), new("gmail", "Gmail", true, false)];
        Assert.Equal(["gmail", "salesforce", "asana", "github"], AppStoreNeuron.Ordered(offers).Select(offer => offer.Id));
    }
}
