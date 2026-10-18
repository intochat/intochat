using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Platform.Connections;
using DigitalBrain.Framework.Surface;
using DigitalBrain.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Mcp.Tests.Integration;

public sealed class NeuronSurfaceFacts(McpHost host)
{
    [Fact]
    public async Task ABadArgumentCountSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await using var app = await Host(brain);
        using var client = Client(app);

        var (text, isError) = await NeuronsCall(client, new
        {
            contract = typeof(IApp).FullName!,
            key = "b/apps/example",
            method = "Read",
            arguments = "[1, 2, 3]"
        }, ct);

        Assert.True(isError);
        Assert.StartsWith("ArgumentException: IApp has no method Read taking 3 arguments.", text);
    }

    [Fact]
    public async Task AGrainExceptionMessageReachesTheCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await using var app = await Host(brain);
        using var client = Client(app);

        var (text, isError) = await NeuronsCall(client, new
        {
            contract = typeof(IApp).FullName!,
            key = "b/apps/absent",
            method = "ExportPublished",
            arguments = "[]"
        }, ct);

        Assert.True(isError);
        Assert.StartsWith("InvalidOperationException: absent has no published version.", text);
    }

    [Fact]
    public async Task AnAppCreatedThroughMcpIsListedAsAnInstanceOfTheCallersBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await using var app = await Host(brain);
        using var client = Client(app);
        var (context, _) = await Tool(client, "neurons_context", new { }, ct);
        var scope = JsonDocument.Parse(context).RootElement.GetProperty("brainId").GetString()!;
        Assert.Contains(scope, JsonDocument.Parse(context).RootElement.GetProperty("brains").EnumerateArray().Select(each => each.GetString()));
        var manifest = JsonSerializer.Serialize(new object[] { new AppManifest("Echo", "echoes", [], [], []), "Echoes." }, JsonSerializerOptions.Web);
        var (created, failed) = await Tool(client, "neurons_call", new { contract = typeof(IApp).FullName!, key = scope + "/apps/echo", method = "Create", arguments = manifest }, ct);
        Assert.False(failed, created);

        var (all, _) = await Tool(client, "neurons_list", new { }, ct);
        var (apps, _) = await Tool(client, "neurons_list", new { contract = typeof(IApp).FullName! }, ct);

        Assert.Contains(scope + "/apps/echo", all);
        var listed = JsonDocument.Parse(apps).RootElement;
        var entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal((typeof(IApp).FullName, scope + "/apps/echo"), (entry.GetProperty("contract").GetString(), entry.GetProperty("key").GetString()));
    }

    [Fact]
    public async Task AKernelGrainIsNotAddressable()
    {
        Assert.DoesNotContain(Deployed.Kernel().Entries, entry => entry.Name == typeof(IConnectionIndex).FullName);
        await Assert.ThrowsAsync<ArgumentException>(() => Surface().Get(typeof(IConnectionIndex).FullName!, "b/x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchThroughMcpAsksTheSilosKernel()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        await using var app = await Host(brain);
        using var client = Client(app);

        var (text, isError) = await Tool(client, "neurons_search", new { query = "app" }, ct);

        Assert.False(isError, text);
        Assert.Contains(typeof(IApp).FullName!, text);
    }

    [Fact]
    public void ToolNamesAndDescriptionsAreDefinedOnce()
    {
        Assert.Equal(["neurons_list", "neurons_search", "neurons_get", "neurons_call", "neurons_context"],
            [NeuronSurfaceTools.ListName, NeuronSurfaceTools.SearchName, NeuronSurfaceTools.GetName, NeuronSurfaceTools.CallName, NeuronSurfaceTools.ContextName]);
        Assert.All([NeuronSurfaceTools.ListDescription, NeuronSurfaceTools.SearchDescription, NeuronSurfaceTools.GetDescription,
            NeuronSurfaceTools.CallDescription, NeuronSurfaceTools.ContextDescription], description => Assert.False(string.IsNullOrWhiteSpace(description)));
    }

    [Fact]
    public async Task WatchIsNotCallable()
    {
        var surface = Surface();
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Apps.IApp).FullName!, "b/apps/x", "Watch", "[null]", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MalformedArgumentsAreAnArgumentError()
    {
        var surface = Surface();
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Apps.IApp).FullName!, "b/apps/x", "Read", "{not json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARestrictedSurfaceRefusesAContractTheAppDidNotName()
    {
        var ct = TestContext.Current.CancellationToken;
        var surface = await Surface().Restrict([typeof(DigitalBrain.Apps.IApp).FullName!], ct);
        Assert.Equal([typeof(DigitalBrain.Apps.IApp).FullName!], await surface.Contracts(ct));
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Postgres.IPostgresServer).FullName!,
            "b/connection/postgres/x", "ReadConnection", "[]", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Get(typeof(DigitalBrain.Postgres.IPostgresServer).FullName!, "b/connection/postgres/x", ct));
    }

    [Fact]
    public async Task RestrictingToAnUnknownContractIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var surface = Surface();
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Restrict(["DigitalBrain.Nope.INope"], ct));
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Restrict([typeof(IConnectionIndex).FullName!], ct));
    }

    [Fact]
    public async Task ARestrictedSearchRanksOnlyWhatTheAppComposes()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var kernel = Deployed.Kernel();
        var surface = await new NeuronSurface(new UnreachableBrain(), kernel, new NeuronTypeSearch(kernel, [new CrowdedSource()], null))
            .Restrict([typeof(IMcpServer).FullName!], ct);
        using var caller = brain.Owner.AsCaller();

        var hits = await surface.Search("render image preview", ct);

        Assert.Equal($"tool:{BrainScope.CurrentId()}/mcp/photocraft/render_preview", Assert.Single(hits).Type.Id);
    }

    // Sixty entries the app does not compose match the query better than its one tool.
    private sealed class CrowdedSource : IRuntimeCapabilitySource
    {
        public Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<NeuronType>>([
                .. Enumerable.Range(0, 60).Select(index => new NeuronType($"other:{index}", "other", "Render image preview " + index, "render image preview", [], "Other.IUnrelated")),
                new NeuronType($"tool:{brainId}/mcp/photocraft/render_preview", "tools", "PhotoCraft: Render", "A flattened PNG of the document image.",
                    [], typeof(IMcpServer).FullName!, brainId + "/mcp/photocraft", "render_preview")]);
    }

    private static NeuronSurface Surface()
        => new(new UnreachableBrain(), Deployed.Kernel(), null!);

    // A real MCP edge over a real brain, open posture so every request acts as the owner.
    private static async Task<WebApplication> Host(ITestBrain brain)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = "Open";
        builder.Services.AddSingleton<IDigitalBrain>(brain);
        builder.Services.AddSingleton(brain.Services.GetRequiredService<IGrainFactory>());
        builder.Services.AddDigitalBrainMcp();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.UseRouting();
        app.UseAuthentication();
        app.UseAccountSession();
        app.MapDigitalBrainMcp();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        return client;
    }

    private static Task<(string Text, bool IsError)> NeuronsCall(HttpClient client, object arguments, CancellationToken ct)
        => Tool(client, "neurons_call", arguments, ct);

    private static async Task<(string Text, bool IsError)> Tool(HttpClient client, string name, object arguments, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name, arguments }
        }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);
        var result = document.RootElement.GetProperty("result");
        var content = result.GetProperty("content");
        var text = content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
            ? content[0].GetProperty("text").GetString() ?? ""
            : "";
        return (text, result.TryGetProperty("isError", out var error) && error.GetBoolean());
    }

    // Both refusals happen before a neuron is resolved, so reaching the brain is a failure.
    private sealed class UnreachableBrain : IDigitalBrain
    {
        public IBrain Self => throw new NotSupportedException();
        public IKernelView Kernel => throw new NotSupportedException();

        public T Get<T>(NeuronId id) where T : INeuron => throw new NotSupportedException();

        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
        public IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
    }
}
