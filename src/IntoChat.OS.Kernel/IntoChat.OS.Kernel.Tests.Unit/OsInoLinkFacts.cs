using IntoChat.OS;
using IntoChat.OS.AI.Agents;
using IntoChat.OS.Flutter.Chat;
using Xunit;

namespace IntoChat.OS.Kernel.Tests.Unit;

// A pasted workspace link becomes an app card with the shell's own parsing rules, and never reaches the model.
public sealed class OsInoLinkFacts
{
    [Fact]
    public void OnlyAWholeWorkspaceLinkBecomesACard()
    {
        Assert.Equal("notes", InoLinks.Parse("#/open?application=notes"));
        Assert.Equal("notes", InoLinks.Parse("https://os.intochat.io/#/open?application=notes&input=x"));
        Assert.Equal("notes", InoLinks.Parse("  #/open?application=notes  "));
        Assert.Null(InoLinks.Parse("see #/open?application=notes"));
        Assert.Null(InoLinks.Parse("#/open?application=notes thanks"));
        Assert.Null(InoLinks.Parse("#/open?application=Bad_Name"));
        Assert.Null(InoLinks.Parse("#/open?application=-notes"));
        Assert.Null(InoLinks.Parse("#/open?application=" + new string('a', 81)));
        Assert.Equal(new string('a', 80), InoLinks.Parse("#/open?application=" + new string('a', 80)));
        Assert.Null(InoLinks.Parse("#/close?application=notes"));
        Assert.Null(InoLinks.Parse("#/open"));
        Assert.Null(InoLinks.Parse("#/open?application=notes&application=other"));
        Assert.Null(InoLinks.Parse(""));
        Assert.Null(InoLinks.Parse("open notes"));
    }

    [Fact]
    public void TheInputStaysOpaque()
    {
        Assert.True(InoLinks.TryParse("https://os.intochat.io/#/open?application=notes&input=a%20b%2Bc%26d", out var app, out var input));
        Assert.Equal("notes", app);
        Assert.Equal("a b+c&d", input);
        Assert.True(InoLinks.TryParse("#/open?application=notes", out _, out var empty));
        Assert.Equal("", empty);
    }

    [Fact]
    public void ALinkPostIdRoundTripsAndNeverLeaksIntoTheText()
    {
        var request = Guid.NewGuid();
        var id = ChatAppCard.LinkPostId(request, "notes", "x <b>&\"y\"");
        Assert.StartsWith("link-" + request.ToString("N"), id, StringComparison.Ordinal);
        Assert.True(ChatAppCard.TryReadLinkPost("post/" + id, out var app, out var input));
        Assert.Equal("notes", app);
        Assert.Equal("x <b>&\"y\"", input);
        Assert.False(ChatAppCard.TryReadLinkPost("post/greeting", out _, out _));
        Assert.False(ChatAppCard.TryReadLinkPost("post/link-zz.Bad_Name.", out _, out _));
        Assert.False(ChatAppCard.TryReadLinkPost("r1", out _, out _));
    }

    [Fact]
    public async Task APastedLinkPostsACardAndStartsNoTurn()
    {
        var agent = new FakeAgent();
        var request = Guid.NewGuid();
        var link = "https://os.intochat.io/#/open?application=notes&input=secret-ish";
        Assert.True(await InoLinks.PostCard(agent, link, request));
        var (text, id) = Assert.Single(agent.Posts);
        Assert.Equal(InoCopy.LinkPosted, text);
        Assert.DoesNotContain("open?application", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-ish", text, StringComparison.Ordinal);
        Assert.Equal(ChatAppCard.LinkPostId(request, "notes", "secret-ish"), id);
        Assert.Equal(0, agent.Starts);

        Assert.False(await InoLinks.PostCard(agent, "please open " + link, Guid.NewGuid()));
        Assert.Single(agent.Posts);
    }

    [Fact]
    public void TheChatRendererUsesInoCopy()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "features", "os", "Ino"))) { root = root.Parent; }
        Assert.NotNull(root);
        var dart = File.ReadAllText(Path.Combine(root.FullName,
            "src/IntoChat.OS.Modules/Google/Flutter/app/ui/lib/src/chat/chat_copy.dart"));
        Assert.Contains("empty = '" + InoCopy.EmptyTranscript + "';", dart, StringComparison.Ordinal);
        Assert.Contains("failed = \"" + InoCopy.TurnFailed + "\";", dart, StringComparison.Ordinal);
        Assert.Contains("notInstalled = '" + InoCopy.AppNotInstalled + "';", dart, StringComparison.Ordinal);
    }

    private sealed class FakeAgent : IAgent
    {
        public List<(string Text, string Id)> Posts { get; } = [];
        public int Starts { get; private set; }
        public Task Post(string text, string id) { Posts.Add((text, id)); return Task.CompletedTask; }
        public Task<string> Start(AgentTurn turn) { Starts++; return Task.FromResult("run"); }
        public Task Configure(AgentDefinition definition, long expectedRevision) => throw new NotSupportedException();
        public IAsyncEnumerable<AgentTurnEvent> Run(AgentTurn turn, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Cancel() => throw new NotSupportedException();
        public Task CancelRun(string runId) => throw new NotSupportedException();
        public Task<AgentState> Read() => throw new NotSupportedException();
        public Task<IntoChat.OS.AI.Inference.AiMessage[]> Transcript() => throw new NotSupportedException();
        public Task Clear() => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<Guid> Watch(INeuronObserver observer, string[] signals) => throw new NotSupportedException();
        public Task Unwatch(INeuronObserver observer) => throw new NotSupportedException();
        public Task<SignalPage> Read(long since, int limit) => throw new NotSupportedException();
    }
}
