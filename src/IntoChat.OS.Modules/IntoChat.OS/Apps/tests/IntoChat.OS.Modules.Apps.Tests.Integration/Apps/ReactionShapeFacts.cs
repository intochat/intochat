using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

// L0: a synapse is exactly one top-level reaction. The check is syntax, and the app gates hold every
// program to it before anything deploys, publishes or exports.
public sealed class ReactionShapeFacts
{
    private const string Brain = "synapse-shape-facts";

    private const string Connect = """
        await using var brain = await IntoChatOSClient.ConnectAsync(args);
        var app = brain.Get<IntoChat.OS.Modules.Apps.IApp>(brain.Setting("App")!);

        """;

    private const string OneReaction = Connect + """
        await foreach (var request in brain.On(app, "ask", brain.Stopping))
        { Console.WriteLine(request.Input); }
        """;

    private const string TwoReactions = Connect + """
        await foreach (var open in brain.On(app, "open", brain.Stopping)) { }
        await foreach (var close in brain.On(app, "close", brain.Stopping)) { }
        """;

    private const string NoReaction = Connect + "await Task.Delay(Timeout.Infinite, brain.Stopping);\n";

    [Fact]
    public void ASynapseWithTwoTopLevelReactionsIsRefused()
    {
        var error = Assert.Single(ReactionShape.Check("twice.cs", TwoReactions));

        Assert.Equal((ReactionShape.SecondReaction, "twice.cs", 4), (error.Id, error.File, error.Line));
        Assert.Contains("line 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASynapseWithoutAReactionIsRefused()
        => Assert.Equal(ReactionShape.NoReaction, Assert.Single(ReactionShape.Check("idle.cs", NoReaction)).Id);

    [Fact]
    public void AReactionInsideALocalFunctionDoesNotCount()
    {
        const string loops = Connect + """
            await Task.WhenAll(Commands());

            async Task Commands()
            { await foreach (var request in brain.On(app, "ask", brain.Stopping)) { } }
            """;

        Assert.Equal(ReactionShape.NoReaction, Assert.Single(ReactionShape.Check("loops.cs", loops)).Id);
    }

    [Fact]
    public void AnInnerLoopInsideTheReactionIsAllowed()
    {
        const string follows = Connect + """
            await foreach (var request in brain.On(app, "follow", brain.Stopping))
            {
                await foreach (var again in brain.On(app, "again", brain.Stopping)) { break; }
                var tries = 0;
                while (tries++ < 3) { await Task.Yield(); }
            }
            """;

        Assert.Empty(ReactionShape.Check("follows.cs", follows));
    }

    [Theory]
    [InlineData("await foreach (var request in brain.On<IntoChat.OS.Modules.Apps.Signals.AppRequested>(app, brain.Stopping)) { }")]
    [InlineData("await foreach (var request in brain.On(app, \"open\", brain.Stopping)) { }")]
    [InlineData("await foreach (var click in brain.On<ButtonClicked>(Sources.Of(a, b), brain.Stopping)) { }")]
    [InlineData("await foreach (var request in brain.On(app, \"open\", brain.Stopping).WithCancellation(brain.Stopping)) { }")]
    [InlineData("await brain.React<TextChanged>(source, Rebuild, brain.Stopping);")]
    public void EveryFormOfReactionIsOneReaction(string reaction)
        => Assert.Empty(ReactionShape.Check("react.cs", Connect + reaction + "\n"));

    [Fact]
    public void SharedCodeAndDataHaveNoShape()
    {
        var files = new Dictionary<string, string>
        {
            ["shared.cs"] = "public static class Text { public static string Upper(string text) => text.ToUpperInvariant(); }",
            ["notes.md"] = "await foreach is prose here",
            [AppFiles.DeclarationPath] = "static class App { }",
            ["ask.cs"] = OneReaction,
        };

        Assert.Empty(ReactionShape.Check(files));
        Assert.True(ReactionShape.IsShared(files["shared.cs"]));
        Assert.False(ReactionShape.IsShared(OneReaction));
    }

    [Fact]
    public void ALayoutClassIsSharedCodeWithAStaticRenderReturningAUiNode()
    {
        Assert.True(ReactionShape.IsLayout("public static class L { public static UiNode Render(int m) => null; }"));
        Assert.False(ReactionShape.IsLayout("public static class Text { public static string Upper(string t) => t; }"));
        Assert.False(ReactionShape.IsLayout(OneReaction));
    }

    [Theory]
    [InlineData("", true, false)]
    [InlineData("// Only a comment", true, false)]
    [InlineData("public static class L { public static UiNode? Render() => null; }", true, true)]
    [InlineData("public static class L { public static Flutter.Views.UiNode Render() => null; }", true, true)]
    [InlineData("public static class L { class Nested { public static Flutter.Views.UiNode? Render() => null; } }", true, true)]
    [InlineData("public class L { public UiNode Render() => null; }", true, false)]
    [InlineData("public static class L { public static string Render() => null; }", true, false)]
    [InlineData("void Helper() { }", false, false)]
    [InlineData("{ await brain.React(source); }", false, false)]
    [InlineData("Console.WriteLine(1); public static class L { public static UiNode Render() => null; }", false, false)]
    public void SharedAndLayoutRolesRespectTopLevelStatementsAndRenderReturnTypes(string source, bool shared, bool layout)
        => Assert.Equal((shared, layout), (ReactionShape.IsShared(source), ReactionShape.IsLayout(source)));

    [Fact]
    public void NullLiteralClassificationKeepsEmptySourceSemantics()
        => Assert.Equal((true, false), (ReactionShape.IsShared(null!), ReactionShape.IsLayout(null!)));

    [Trait("host", "own")]
    [Fact]
    public async Task AWriteThatIsNotOneReactionIsNeverDeployed()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var brain = await FakeAppRuntime.Start(runtime, ct);
        var app = await Created(brain, "twice");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.WriteBehavior("twice.cs", TwoReactions, Guid.NewGuid()));

        Assert.Contains("one reaction", refused.Message, StringComparison.Ordinal);
        Assert.Contains("twice.cs:4", refused.Message, StringComparison.Ordinal);
        Assert.Empty(runtime.Deployed);
        Assert.DoesNotContain("twice.cs", (await app.Read()).Files.Keys);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ABuildThatIsNotOneReactionGoesFailedAndNeverDeploys()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new FakeAppRuntime();
        await using var brain = await FakeAppRuntime.Start(runtime, ct);
        var app = await Created(brain, "idle");
        await app.BeginBuild(null);

        var failed = await app.CompleteBuild(await app.CurrentBuildAttempt(), new() { ["idle.cs"] = NoReaction }, null);

        Assert.Equal(AppStatus.Failed, failed.Status);
        Assert.Contains(failed.Activity, line => line.Contains("idle.cs:", StringComparison.Ordinal) && line.Contains("one reaction", StringComparison.Ordinal));
        Assert.Empty(runtime.Deployed);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ExportRefusesAProgramThatIsNotOneReactionEvenWithoutACompiler()
    {
        var ct = TestContext.Current.CancellationToken;
        // No runtime and no sandbox: the program is data, and the shape still gates what leaves the brain.
        await using var brain = await TestBrain.Create().WithModule<AppsModule>()
            .ConfigureServices(services => services.RemoveAll<IAppRuntime>()).StartAsync(ct);
        var app = await Created(brain, "data");
        await app.WriteBehavior("idle.cs", NoReaction, Guid.NewGuid());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(app.Export);

        Assert.Contains("not shaped as reactions", refused.Message, StringComparison.Ordinal);
        Assert.Contains("idle.cs:", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<IApp> Created(ITestBrain brain, string name)
    {
        var app = brain.Get<IApp>(IApp.Key(Brain, name));
        await app.Create(new(name, "shape facts", [new("ask", "ask it"), new("open", "open it"), new("close", "close it")], [], []), "spec");
        return app;
    }

    private static readonly Dictionary<string, string[]> Provides = new(StringComparer.Ordinal)
    {
        ["google-drive"] = ["storage@1"],
        ["windows"] = ["storage@1"],
        ["postgres"] = [],
    };

    private static string Declaring(string requires, string slots = "[]") => $$"""
        static class App
        {
            public const string Name = "files";
            public const string Version = "1.0.0";
            public static readonly AppManifest Manifest = new("Files", "", [], [], {{slots}}, Requires: {{requires}});
        }
        """;

    [Fact]
    public void AModuleIdThatProvidesACapabilityIsRxn0003()
    {
        var error = Assert.Single(ReactionShape.CheckRequires("app.cs", Declaring("""new(Connectors: ["google-drive"])"""), Provides));
        Assert.Equal(ReactionShape.ModuleIdRequired, error.Id);
        Assert.Equal("RXN0003", error.Id);
        Assert.StartsWith("google-drive provides storage@1; require the capability instead", error.Message, StringComparison.Ordinal);

        var slot = Assert.Single(ReactionShape.CheckRequires("app.cs",
            Declaring("""new(Capabilities: ["storage@1"])""", """[new("pc", "windows")]"""), Provides));
        Assert.Equal("RXN0003", slot.Id);
        Assert.StartsWith("windows provides storage@1", slot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilitiesAndAllowedModulesAreNotRxn0003()
    {
        Assert.Empty(ReactionShape.CheckRequires("app.cs", Declaring("""new(Connectors: ["postgres"])"""), Provides));
        Assert.Empty(ReactionShape.CheckRequires("app.cs", Declaring("""new(Capabilities: ["storage@1"])""", """[new("drive", Capability: "storage@1")]"""), Provides));
        // A module id outside the allow-list is a vendor id even when its module is unknown here.
        Assert.Equal("RXN0003", Assert.Single(ReactionShape.CheckRequires("app.cs", Declaring("""new(Connectors: ["mailbox"])"""))).Id);
    }
}

// The modules an app may still name directly: only those that provide no capability yet (05-10).
public sealed class VendorIdAllowListFacts
{
    [Fact]
    public void OnlyPostgresMayBeNamed() => Assert.Equal(["postgres"], RequiredModuleIds.Allowed);
}
