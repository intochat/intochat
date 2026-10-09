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

namespace DigitalBrain.Mcp.Tests.Unit;

public sealed class NeuronSurfaceFacts
{
    [Fact]
    public async Task ABadArgumentCountSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
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
        await using var brain = await ModuleTest.Create().WithModule<AppsModule>().StartAsync(ct);
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
    public void APlatformOnlyContractIsNotAddressable()
    {
        var contracts = new ContractCatalog(ContractCatalog.Deployed());
        Assert.DoesNotContain(typeof(IConnectionIndex).FullName!, contracts.Neurons);
        Assert.Throws<ArgumentException>(() => contracts.Find(typeof(IConnectionIndex).FullName!));
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
        var surface = Surface()
            .Restrict([typeof(DigitalBrain.Apps.IApp).FullName!]);
        Assert.Equal([typeof(DigitalBrain.Apps.IApp).FullName!], surface.List());
        await Assert.ThrowsAsync<ArgumentException>(() => surface.Call(typeof(DigitalBrain.Postgres.IPostgresServer).FullName!,
            "b/connection/postgres/x", "ReadConnection", "[]", TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => surface.Get(typeof(DigitalBrain.Postgres.IPostgresServer).FullName!, "b/connection/postgres/x"));
    }

    [Fact]
public void RestrictingToAnUnknownContractIsRefused()
    {
        var surface = Surface();
        Assert.Throws<ArgumentException>(() => surface.Restrict(["DigitalBrain.Nope.INope"]));
        Assert.Throws<ArgumentException>(() => surface.Restrict([typeof(IConnectionIndex).FullName!]));
    }

    [Fact]
    public async Task ARestrictedSearchRanksOnlyWhatTheAppComposes()
    {
        var ct = TestContext.Current.CancellationToken;
        var contracts = new ContractCatalog(ContractCatalog.Deployed());
        var surface = new NeuronSurface(new UnreachableBrain(), new CapabilityCatalog(contracts, [new CrowdedSource()]),
            new NeuronTypeSearch(contracts, null)).Restrict([typeof(IMcpServer).FullName!]);
        CallerContextStamper.Stamp(new()
        { PrincipalId = "owner", AccountId = "owner", BrainId = "crowded", Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });

        var hits = await surface.Search("render image preview", ct);

        Assert.Equal("tool:crowded/mcp/photocraft/render_preview", Assert.Single(hits).Type.Id);
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
        => new(new UnreachableBrain(), new CapabilityCatalog(new ContractCatalog(ContractCatalog.Deployed()), []), null!);

    // A real MCP edge over a real brain, open posture so every request acts as the owner.
    private static async Task<WebApplication> Host(ModuleBrain brain)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration[AuthOptions.PostureKey] = "Open";
        builder.Services.AddSingleton<IDigitalBrain>(brain);
        builder.Services.AddSingleton(brain.Grains);
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

    private static async Task<(string Text, bool IsError)> NeuronsCall(HttpClient client, object arguments, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "neurons_call", arguments }
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
        public IKernel Self => throw new NotSupportedException();

        public T Get<T>(NeuronId id) where T : INeuron => throw new NotSupportedException();

        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
        public IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
    }
}
