using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DigitalBrain.Testing;
using IntoChat.OS.Apps.FilmCraft;
using IntoChat.OS.Apps.Ino;
using IntoChat.OS.Apps.PhotoCraft;
using IntoChat.OS.Modules.AI.Context;
using IntoChat.OS.Modules.AI.Decision;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class InoDecisionFacts
{
    private sealed class MockDecisionMaker(string returnChoice, double returnConfidence) : IDecisionMaker
    {
        public DecisionContext? LastContext { get; private set; }

        public Task<DecisionResult> DecideAsync(DecisionContext context, CancellationToken ct = default)
        {
            LastContext = context;
            return Task.FromResult(new DecisionResult(returnChoice, returnConfidence, DecisionMode.Reactive));
        }

        public Task<FastDecision> DecideReactiveAsync(ReactivePrompt prompt, CancellationToken ct = default)
            => Task.FromResult(new FastDecision(returnChoice, returnConfidence, new Dictionary<string, double> { [returnChoice] = returnConfidence }));

        public Task<DeliberateDecision> DecideDeliberateAsync(DeliberateGoal goal, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task InoAppRoutesViaDecisionMakerWhenConfigured_AndPassesRichCandidateDescriptions()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        var mock = new MockDecisionMaker("photocraft", 0.95);
        ino.App.DecisionMaker = mock;

        await ino.Request("send", new InoSend("Make a vintage poster with sunset"));

        Assert.NotNull(mock.LastContext);
        Assert.Equal("Make a vintage poster with sunset", mock.LastContext.Intent);
        Assert.NotNull(mock.LastContext.CandidateDescriptions);
        foreach (var candidate in mock.LastContext.Candidates)
        {
            Assert.True(
                mock.LastContext.CandidateDescriptions.TryGetValue(candidate, out var desc) && !string.IsNullOrWhiteSpace(desc),
                $"Candidate '{candidate}' must have a non-empty semantic description.");
        }

        Assert.Contains(ino.App.State.Turns, t => t.RouteHint.Contains("photocraft", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("hey, what can you do?")]
    [InlineData("what can you do?")]
    [InlineData("who are you?")]
    [InlineData("help")]
    [InlineData("what apps are installed?")]
    public async Task CapabilityAndMetaQuestions_RouteToGeneralChat_AndDiscoverLiveCapabilities_NeverPhotoCraft(string prompt)
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        var services = new ServiceCollection().BuildServiceProvider();
        var decisionMaker = new DecisionMaker(services, new ExecutionContextProvider(), NullLogger<DecisionMaker>.Instance);
        ino.App.DecisionMaker = decisionMaker;
        ino.App.FilesVfs = WorkspaceFilesService.ForBrain(services, $"brain-cap-{Guid.NewGuid():N}");

        await ino.Request("send", new InoSend(prompt));

        var turn = Assert.Single(ino.App.State.Turns);
        Assert.StartsWith("agent:", turn.RouteHint, StringComparison.Ordinal);
        Assert.DoesNotContain("photocraft", turn.RouteHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photocraft-cli", turn.Reply, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/work/portrait", turn.Reply, StringComparison.OrdinalIgnoreCase);

        // Must dynamically introspect and list installed capabilities
        Assert.Contains("Files", turn.Reply, StringComparison.Ordinal);
        Assert.Contains("PhotoCraft", turn.Reply, StringComparison.Ordinal);
        Assert.Contains("FilmCraft", turn.Reply, StringComparison.Ordinal);
        Assert.Contains("Settings", turn.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArtCraftEngineRunner_RejectsUnrecognizedConversationalPrompts_WithoutFabricatingRemoveBackground()
    {
        var photoResult = await ArtCraftEngineRunner.ExecutePhotoCraftAsync(
            "hey, what can you do?",
            explicitParamsJson: null,
            currentDocument: null,
            executionMode: "native-linux",
            studio: null,
            mcpResolver: null);

        Assert.False(photoResult.IsValidCommand);
        Assert.Equal("clarification.required", photoResult.CommandId);
        Assert.Empty(photoResult.CliInvocation);
        Assert.Empty(photoResult.OutputDocument);
        Assert.DoesNotContain("layer.removeBackground", photoResult.Summary, StringComparison.OrdinalIgnoreCase);

        var filmResult = await ArtCraftEngineRunner.ExecuteFilmCraftAsync(
            "hey, what can you do?",
            explicitParamsJson: null,
            currentSequence: null,
            executionMode: "native-linux",
            studio: null,
            mcpResolver: null);

        Assert.False(filmResult.IsValidCommand);
        Assert.Equal("clarification.required", filmResult.CommandId);
        Assert.Empty(filmResult.CliInvocation);
        Assert.Empty(filmResult.OutputDocument);
    }

    [Fact]
    public async Task UnifiedFilesVfs_ConnectsInoPhotoCraftAndFilmCraft_RoundtripWriteListReadAndEdit()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var brainId = $"brain-vfs-{Guid.NewGuid():N}";
        var vfs = WorkspaceFilesService.ForBrain(null, brainId);

        var ino = await os.Install<InoApp>();
        var photoCraft = await os.Install<PhotoCraftApp>();
        var filmCraft = await os.Install<FilmCraftApp>();

        ino.App.FilesVfs = vfs;
        photoCraft.App.FilesVfs = vfs;
        filmCraft.App.FilesVfs = vfs;

        // 1. Ino creates a text file in Files VFS
        await ino.Request("send", new InoSend("Create file architecture.md with content Unified VFS across Ino and Studio apps"));
        Assert.Equal("routed:files.resource", ino.App.State.Turns[^1].RouteHint);
        Assert.Contains("Saved **architecture.md**", ino.App.State.Turns[^1].Reply, StringComparison.Ordinal);

        // 2. Ino reads the file back from Files VFS
        await ino.Request("send", new InoSend("Read architecture.md"));
        Assert.Equal("routed:files.resource", ino.App.State.Turns[^1].RouteHint);
        Assert.Contains("Unified VFS across Ino and Studio apps", ino.App.State.Turns[^1].Reply, StringComparison.Ordinal);

        // 3. Seed an image in Files VFS and edit it through Ino -> PhotoCraft
        await vfs.WriteFileAsync("nordlys-team.png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, TestContext.Current.CancellationToken);
        await ino.Request("send", new InoSend("Remove background from nordlys-team.png"));

        var photoTurn = ino.App.State.Turns[^1];
        Assert.Equal("routed:photocraft.layer.removeBackground", photoTurn.RouteHint);
        Assert.Contains("/work/nordlys-team.nobg.png", photoTurn.Reply, StringComparison.Ordinal);

        // Verify edited output artifact was persisted into the shared Files VFS
        var resolvedEdited = await vfs.ResolveFileAsync("nordlys-team.nobg.png", TestContext.Current.CancellationToken);
        Assert.NotNull(resolvedEdited);
        Assert.Equal("nordlys-team.nobg.png", resolvedEdited.Name);

        // 4. Ino lists workspace files and sees architecture.md, nordlys-team.png, and nordlys-team.nobg.png
        await ino.Request("send", new InoSend("List workspace files"));
        var listTurn = ino.App.State.Turns[^1];
        Assert.Equal("routed:files.resource", listTurn.RouteHint);
        Assert.Contains("architecture.md", listTurn.Reply, StringComparison.Ordinal);
        Assert.Contains("nordlys-team.png", listTurn.Reply, StringComparison.Ordinal);
        Assert.Contains("nordlys-team.nobg.png", listTurn.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InoAppFallsBackToPatternMatchingWhenDecisionMakerNull()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        ino.App.DecisionMaker = null;

        await ino.Request("send", new InoSend("Remove background from photo"));

        Assert.Contains(ino.App.State.Turns, t => t.RouteHint.Contains("photocraft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InoAppFallsBackForFilmCraftWhenDecisionMakerNull()
    {
        await using var os = TestOS.Local();
        os.Register(typeof(AppsModule).Assembly);

        var ino = await os.Install<InoApp>();
        ino.App.DecisionMaker = null;

        await ino.Request("send", new InoSend("Trim video at 5 seconds"));

        Assert.Contains(ino.App.State.Turns, t => t.RouteHint.Contains("filmcraft", StringComparison.OrdinalIgnoreCase));
    }
}
