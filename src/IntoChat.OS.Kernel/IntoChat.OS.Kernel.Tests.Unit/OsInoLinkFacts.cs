using IntoChat.OS;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.Flutter.Chat;
using IntoChat.OS.Flutter.Text;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

// A pasted workspace link becomes an app card with the shell's own parsing rules, and never reaches the model.
public sealed class OsInoLinkFacts
{
    [Fact]
    public void OnlyAWholeWorkspaceLinkBecomesACard()
    {
        Assert.Equal("notes", InoLinks.Parse("#/open?application=notes")?.App);
        Assert.Equal("notes", InoLinks.Parse("https://os.intochat.io/#/open?application=notes&input=x")?.App);
        Assert.Equal("notes", InoLinks.Parse("  #/open?application=notes  ")?.App);
        Assert.Null(InoLinks.Parse("see #/open?application=notes"));
        Assert.Null(InoLinks.Parse("#/open?application=notes thanks"));
        Assert.Null(InoLinks.Parse("#/open?application=Bad_Name"));
        Assert.Null(InoLinks.Parse("#/open?application=-notes"));
        Assert.Null(InoLinks.Parse("#/open?application=" + new string('a', 81)));
        Assert.Equal(new string('a', 80), InoLinks.Parse("#/open?application=" + new string('a', 80))?.App);
        Assert.Null(InoLinks.Parse("#/close?application=notes"));
        Assert.Null(InoLinks.Parse("#/open"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&application=other"));
        Assert.Null(InoLinks.Parse(""));
        Assert.Null(InoLinks.Parse("open notes"));
    }

    [Fact]
    public void ParseMirrorsTheShellRules()
    {
        var link = InoLinks.Parse("#/open?application=notes&publisher=vlad&release=1.2.3&input=x");
        Assert.Equal(new InoLink("notes", "vlad", "1.2.3", "x"), link);
        Assert.Equal(new InoLink("notes", "vlad", null, ""), InoLinks.Parse("#/open?application=notes&publisher=vlad"));
        Assert.Equal(new InoLink("notes", null, null, ""), InoLinks.Parse("#/open?application=notes"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&publisher=Vlad"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&publisher=-vlad"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&publisher=" + new string('a', 65)));
        Assert.NotNull(InoLinks.Parse("#/open?application=notes&publisher=" + new string('a', 64)));
        Assert.Null(InoLinks.Parse("#/open?application=notes&publisher=vlad&release=1.0"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&release=1.0.0"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&publisher=vlad&publisher=other"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&input=" + new string('x', 2049)));
        Assert.NotNull(InoLinks.Parse("#/open?application=notes&input=" + new string('x', 2048)));
    }

    [Fact]
    public void ParseGrammarMatchesTheAppsRules()
    {
        const string head = "#/open?application=notes&publisher=vlad";
        Assert.Null(InoLinks.Parse(head + "%0A"));
        Assert.Null(InoLinks.Parse("#/open?application=notes%0A"));
        Assert.Null(InoLinks.Parse(head + "&release=1.2.3%0A"));
        Assert.Null(InoLinks.Parse(head + "&release=%D9%A1.%D9%A2.%D9%A3"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&input=a&input=b"));
        Assert.Null(InoLinks.Parse(head + "&release=1.0.0&release=2.0.0"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&publisher=1vlad"));
        Assert.Null(InoLinks.Parse("#/open?application=1notes&publisher=vlad"));
        Assert.Null(InoLinks.Parse("#/open?application=" + new string('a', 65) + "&publisher=vlad"));
        Assert.NotNull(InoLinks.Parse("#/open?application=" + new string('a', 64) + "&publisher=vlad"));
        Assert.NotNull(InoLinks.Parse("#/open?application=my_app&publisher=my_pub"));
        Assert.Null(InoLinks.Parse(head + "&release=1234567890.0.0"));
        Assert.NotNull(InoLinks.Parse(head + "&release=123456789.0.0"));
    }

    [Fact]
    public void TheInputStaysOpaque()
    {
        var link = InoLinks.Parse("https://os.intochat.io/#/open?application=notes&input=a%20b%2Bc%26d");
        Assert.Equal("notes", link?.App);
        Assert.Equal("a b+c&d", link?.Input);
        Assert.Equal("", InoLinks.Parse("#/open?application=notes")?.Input);
    }

    [Fact]
    public void ALinkPostIdIsPlainAndCarriesNoInput()
    {
        var request = Guid.NewGuid();
        var input = "x <b>&\"y\" " + new string('q', 2000);
        var id = ChatAppCard.LinkPostId(request);
        Assert.Equal("link-" + request.ToString("N"), id);
        Assert.True(("post/" + id).Length <= 200);
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        Assert.DoesNotContain(Convert.ToBase64String(bytes)[..16], id, StringComparison.Ordinal);
        Assert.DoesNotContain(System.Buffers.Text.Base64Url.EncodeToString(bytes)[..16], id, StringComparison.Ordinal);
        Assert.True(ChatAppCard.TryReadLinkPost("post/" + id, out var back));
        Assert.Equal(request, back);
        Assert.False(ChatAppCard.TryReadLinkPost("post/greeting", out _));
        Assert.False(ChatAppCard.TryReadLinkPost("post/link-zz", out _));
        Assert.False(ChatAppCard.TryReadLinkPost("r1", out _));
    }

    [Fact]
    public void TheLinksStoreKeepsTheNewest200()
    {
        string? json = null;
        var first = Guid.NewGuid();
        json = ChatAppCard.RememberLink(json, first, "notes", "a");
        json = ChatAppCard.RememberLink(json, first, "other", "b");
        Assert.Equal(new ChatAppCard("notes", "notes", "", true, "a"), ChatAppCard.FindLink(json, first));
        var requests = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var request in requests) { json = ChatAppCard.RememberLink(json, request, "notes", "i"); }
        Assert.Null(ChatAppCard.FindLink(json, first));
        Assert.NotNull(ChatAppCard.FindLink(json, requests[0]));
        Assert.NotNull(ChatAppCard.FindLink(json, requests[^1]));
        Assert.Throws<ArgumentException>(() => ChatAppCard.RememberLink(null, Guid.NewGuid(), "notes", new string('x', 2049)));
        Assert.Null(ChatAppCard.FindLink("not json", first));
        Assert.Equal("b/apps/ino-data/links", ChatAppCard.LinksKey("b/apps/ino-data/conversations/c-1"));
        Assert.Null(ChatAppCard.LinksKey("b/apps/other/agent"));
    }

    [Fact]
    public async Task APastedLinkPostsACardAndStartsNoTurn()
    {
        var world = new World();
        var request = Guid.NewGuid();
        var link = "https://os.intochat.io/#/open?application=notes&input=secret-ish";
        Assert.Equal(InoLinkOutcome.Posted, await world.Handle(link, request));
        var (text, id) = Assert.Single(world.Agent.Posts);
        Assert.Equal(InoCopy.LinkPosted, text);
        Assert.Equal("link-" + request.ToString("N"), id);
        Assert.DoesNotContain("secret-ish", text + id, StringComparison.Ordinal);
        Assert.Equal(0, world.Agent.Starts);
        Assert.Equal(new ChatAppCard("notes", "notes", "", true, "secret-ish"), ChatAppCard.FindLink(world.Links.Markdown, request));
        // The "Heard you" is settled: the status line is cleared and the activity completes.
        Assert.Equal("", world.Status.Markdown);
        Assert.Equal(("completed", InoCopy.LinkPosted), world.Advanced[^1]);

        Assert.Equal(InoLinkOutcome.NotALink, await world.Handle("please open " + link, Guid.NewGuid()));
        Assert.Single(world.Agent.Posts);
    }

    [Fact]
    public async Task ALinkWaitsForTheActiveRunAndARepeatPostsOnce()
    {
        var world = new World();
        world.Agent.Active = true;
        var request = Guid.NewGuid();
        Assert.Equal(InoLinkOutcome.Busy, await world.Handle("#/open?application=notes", request));
        Assert.Empty(world.Agent.Posts);
        Assert.Equal("", world.Links.Markdown);
        Assert.Equal(("attention", InoCopy.AnswerRunning), world.Advanced[^1]);

        world.Agent.Active = false;
        Assert.Equal(InoLinkOutcome.Posted, await world.Handle("#/open?application=notes", request));
        Assert.Equal(InoLinkOutcome.Posted, await world.Handle("#/open?application=notes", request));
        Assert.Single(world.Agent.Posts);
        Assert.Equal(0, world.Agent.Starts);
    }

    // The scripts are top-level programs, so their order is pinned on the source: the composer path handles a link
    // after its active-run check and before any turn; the send path before Jev and the model.
    [Fact]
    public void TheScriptsHandleLinksAfterTheActiveRunCheckAndBeforeAnyTurn()
    {
        var ino = Path.Combine(Root().FullName, "features", "os", "Ino");
        var submissions = File.ReadAllText(Path.Combine(ino, "submissions.cs"));
        var busy = submissions.IndexOf("state.ActiveRun is not null", StringComparison.Ordinal);
        var link = submissions.IndexOf("InoLinks.Handle(", StringComparison.Ordinal);
        var start = submissions.IndexOf("agent.Start(", StringComparison.Ordinal);
        Assert.True(busy >= 0 && busy < link && link < start);
        var send = File.ReadAllText(Path.Combine(ino, "send.cs"));
        link = send.IndexOf("InoLinks.Handle(", StringComparison.Ordinal);
        Assert.True(link >= 0 && link < send.IndexOf("Ino.Touched(", StringComparison.Ordinal)
            && link < send.IndexOf("router.Ask(", StringComparison.Ordinal));
    }

    [Fact]
    public void PostedEntriesAreNeverCapturedSoNoEpisodeCarriesALink()
    {
        var request = Guid.NewGuid();
        var posted = new AgentRun("post/" + ChatAppCard.LinkPostId(request), AgentRunStatus.Completed, "", DateTimeOffset.UtcNow,
            Reply: InoCopy.LinkPosted)
        { Posted = true };
        Assert.False(Episodes.Captures(posted));
        Assert.False(Episodes.Captures(posted with { Posted = false }));
        Assert.True(Episodes.Captures(new AgentRun("r1", AgentRunStatus.Completed, "hi", DateTimeOffset.UtcNow, Reply: "yo")));
        // Even the episode id a capture would use names only the request, never the link's input.
        Assert.DoesNotContain("secret-ish", Episodes.Id("b/apps/ino-data/conversations/main", posted.RunId), StringComparison.Ordinal);
        var os = Path.Combine(Root().FullName, "features", "os");
        Assert.Contains("runId.StartsWith(\"post/\"", File.ReadAllText(Path.Combine(os, "Ino", "captured.cs")), StringComparison.Ordinal);
        Assert.Contains("ask.RunId.StartsWith(\"post/\"", File.ReadAllText(Path.Combine(os, "Memory", "capture.cs")), StringComparison.Ordinal);
    }

    private static DirectoryInfo Root()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "features", "os", "Ino"))) { root = root.Parent; }
        return root ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private sealed class World
    {
        public FakeAgent Agent { get; } = new();
        public FakeText Links { get; } = new();
        public FakeText Status { get; } = new() { Markdown = "Heard you" };
        public List<(string State, string Detail)> Advanced { get; } = [];

        public Task<InoLinkOutcome> Handle(string message, Guid request) => InoLinks.Handle(Agent, Links, Status,
            (state, detail) => { Advanced.Add((state, detail)); return Task.CompletedTask; }, message, request);
    }

    private sealed class FakeText : IText
    {
        public string Markdown { get; set; } = "";
        public int Version { get; private set; }
        public Task Set(string markdown) { Markdown = markdown; Version++; return Task.CompletedTask; }
        public Task<bool> TrySet(string markdown, int expectedVersion)
        {
            if (expectedVersion != Version) { return Task.FromResult(false); }
            Markdown = markdown;
            Version++;
            return Task.FromResult(true);
        }
        public Task<TextState> Read() => Task.FromResult(new TextState { Markdown = Markdown, Version = Version });
        public Task<Guid> Watch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer, string[] signals) => throw new NotSupportedException();
        public Task Unwatch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<SignalPage> Read(long since, int limit) => throw new NotSupportedException();
    }

    [Fact]
    public void TheChatRendererUsesInoCopy()
    {
        var dart = File.ReadAllText(Path.Combine(SolutionProjects.DirectoryOf("IntoChat.OS.Modules.Flutter"),
            "..", "app", "ui", "lib/src/chat/chat_copy.dart"));
        Assert.Contains("empty = '" + InoCopy.EmptyTranscript + "';", dart, StringComparison.Ordinal);
        Assert.Contains("failed = \"" + InoCopy.TurnFailed + "\";", dart, StringComparison.Ordinal);
        Assert.Contains("notInstalled = '" + InoCopy.AppNotInstalled + "';", dart, StringComparison.Ordinal);
        Assert.Contains("install = '" + InoCopy.AppInstall + "';", dart, StringComparison.Ordinal);
        Assert.DoesNotContain("gallery", InoCopy.Greeting, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stepFailed = \"" + InoCopy.StepFailed + "\";", dart, StringComparison.Ordinal);
    }

    private sealed class FakeAgent : IAgent
    {
        public List<(string Text, string Id)> Posts { get; } = [];
        public int Starts { get; private set; }
        public bool Active { get; set; }
        public Task Post(string text, string id) { Posts.Add((text, id)); return Task.CompletedTask; }
        public Task<string> Start(AgentTurn turn) { Starts++; return Task.FromResult("run"); }
        public Task Configure(AgentDefinition definition, long expectedRevision) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Cancel() => throw new NotSupportedException();
        public Task CancelRun(string runId) => throw new NotSupportedException();
        public Task<AgentState> Read() => Task.FromResult(new AgentState(1, new AgentDefinition(), null,
            Active ? new AgentRun("run", AgentRunStatus.Running, "q", DateTimeOffset.UtcNow) : null,
            [.. Posts.Select(post => new AgentRun("post/" + post.Id, AgentRunStatus.Completed, "", DateTimeOffset.UtcNow, Reply: post.Text) { Posted = true })]));
        public Task<IntoChat.OS.AI.Inference.AiMessage[]> Transcript() => throw new NotSupportedException();
        public Task Clear() => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer, string[] signals) => throw new NotSupportedException();
        public Task Unwatch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<SignalPage> Read(long since, int limit) => throw new NotSupportedException();
    }
}
