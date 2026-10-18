using IntoChat.OS.AI;
using IntoChat.OS.AI.Inference;
using IntoChat.OS.AI.Models;
using IntoChat.OS.AI.Models.OpenAI;
using IntoChat.OS.Testing.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Models;

// The fake sits behind the real OpenAI adapter: catalog, provider factory, streaming, usage.
public sealed class FakeModelFacts
{
    private const string Luna = "gpt-5.6-luna";
    private const string Sol = "gpt-5.6-sol";

    private static async Task<IChatClient> Client(ITestBrain brain, string marker, CancellationToken ct)
    {
        var catalog = brain.Services.GetRequiredService<ModelCatalog>();
        return await catalog.CreateClient(await catalog.Resolve(new(marker), requiresTools: false, ct), ct);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnscriptedModelEchoesTheLastUserMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);

        var answer = await brain.Get<IGpt56Luna>("default").Generate(new([new("user", [new AiText("hello")])]), ct);

        Assert.Equal("You said: hello", Assert.IsType<AiText>(Assert.Single(Assert.Single(answer.Messages).Content)).Text);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ScriptsAreKeptPerModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(Luna, "from luna");
        model.Script(Sol, "from sol");
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);

        using var sol = await Client(brain, nameof(IGpt56Sol), ct);
        using var luna = await Client(brain, nameof(IGpt56Luna), ct);

        Assert.Equal("from sol", (await sol.GetResponseAsync("hi", cancellationToken: ct)).Text);
        Assert.Equal("from luna", (await luna.GetResponseAsync("hi", cancellationToken: ct)).Text);
        Assert.Single(model.Requests(Luna));
        Assert.Single(model.Requests(Sol));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStreamedReplyReportsUsageToTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(Luna, "streamed");
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);
        using var client = await Client(brain, nameof(IGpt56Luna), ct);

        var updates = await client.GetStreamingResponseAsync("hi", cancellationToken: ct).ToListAsync(ct);
        var response = updates.ToChatResponse();

        Assert.Equal("streamed", response.Text);
        Assert.Equal(2, response.Usage?.TotalTokenCount);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AStreamedToolCallReachesTheAgentAsAFunctionCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.StartAsync(ct);
        model.Script(Luna, FakeReply.Tool("lookup", """{"q":"x"}"""));
        await using var brain = await TestBrain.Create().WithModule<AIModule>().WithFakeModel(model).StartAsync(ct);
        using var client = await Client(brain, nameof(IGpt56Luna), ct);

        var response = (await client.GetStreamingResponseAsync("hi", cancellationToken: ct).ToListAsync(ct)).ToChatResponse();

        var call = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Equal("lookup", call.Name);
        Assert.Equal("x", call.Arguments!["q"]!.ToString());
    }
}
