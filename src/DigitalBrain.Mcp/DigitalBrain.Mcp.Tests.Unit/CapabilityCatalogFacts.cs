using DigitalBrain.Framework.Contracts;
using Microsoft.Extensions.AI;

namespace DigitalBrain.Mcp.Tests.Unit;

public sealed class CapabilityCatalogFacts
{
    [Fact]
    public async Task WithoutSourcesTheCatalogIsTheContracts()
    {
        var ct = TestContext.Current.CancellationToken;
        var contracts = new ContractCatalog(ContractCatalog.Deployed());
        var catalog = new CapabilityCatalog(contracts, []);
        var read = await catalog.Read("any-brain", ct);
        Assert.Equal(contracts.Read().Select(type => type.Id).ToArray(), read.Select(type => type.Id).ToArray());
    }

    [Fact]
    public async Task ASourcesEntriesFollowTheContractsForItsBrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var contracts = new ContractCatalog(ContractCatalog.Deployed());
        var catalog = new CapabilityCatalog(contracts, [new OneBrainSource()]);
        var one = await catalog.Read("one", ct);
        Assert.Contains(one, type => type.Id == "tool:one/mcp/x/list" && type.Key == "one/mcp/x" && type.Tool == "command_list");
        Assert.Equal([.. contracts.Read().Select(type => type.Id), "tool:one/mcp/x/list"], [.. one.Select(type => type.Id)]);
        var two = await catalog.Read("two", ct);
        Assert.Equal(contracts.Read().Select(type => type.Id).ToArray(), two.Select(type => type.Id).ToArray());
    }

    [Fact]
    public async Task AFailingSourceLeavesTheContractsSearchable()
    {
        var ct = TestContext.Current.CancellationToken;
        var contracts = new ContractCatalog(ContractCatalog.Deployed());
        var catalog = new CapabilityCatalog(contracts, [new FailingSource()]);
        var read = await catalog.Read("one", ct);
        Assert.Equal(contracts.Read().Select(type => type.Id).ToArray(), read.Select(type => type.Id).ToArray());
    }

    [Fact]
    public async Task WordSearchFindsARuntimeToolByItsDescription()
    {
        var ct = TestContext.Current.CancellationToken;
        var search = new NeuronTypeSearch(new ContractCatalog(ContractCatalog.Deployed()), null);
        var tool = new NeuronType("tool:one/mcp/x/sharpen", "tools", "Sharpen", "sharpen the image",
            ["Task<ToolResult> Run(\"sharpen\", arguments)"], "DigitalBrain.Tools.IToolSource", "one/mcp/x", "sharpen");
        var hit = Assert.Single(await search.Search("sharpen the image", [tool], ct: ct));
        Assert.Equal("tool:one/mcp/x/sharpen", hit.Type.Id);
        Assert.Equal(("one/mcp/x", "sharpen"), (hit.Type.Key, hit.Type.Tool));
    }

    [Fact]
    public async Task EmbeddingsAreGeneratedOncePerEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new CountingEmbeddings();
        var search = new NeuronTypeSearch(new ContractCatalog(ContractCatalog.Deployed()), embedder);
        IReadOnlyList<NeuronType> first = [Tool("a"), Tool("b")];
        await search.Search("paint", first, ct: ct);
        IReadOnlyList<NeuronType> second = [Tool("a"), Tool("b"), Tool("c")];
        await search.Search("paint", second, ct: ct);
        // The second search embeds only the new entry and the query; a and b come from the cache.
        Assert.Equal(new[] { 2, 1, 1, 1 }, embedder.Batches.Select(batch => batch.Count).ToArray());
    }

    private static NeuronType Tool(string name)
        => new($"tool:k/{name}", "tools", $"Tool {name}", $"does {name}", [$"run {name}"], "DigitalBrain.Tools.IToolSource", "k", name);

    private sealed class OneBrainSource : IRuntimeCapabilitySource
    {
        public Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<NeuronType>>(brainId == "one"
                ? [new("tool:one/mcp/x/list", "tools", "List", "list engine commands",
                    ["Task<ToolResult> Run(\"command_list\", arguments)"], "DigitalBrain.Tools.IToolSource", "one/mcp/x", "command_list")]
                : []);
    }

    private sealed class FailingSource : IRuntimeCapabilitySource
    {
        public Task<IReadOnlyList<NeuronType>> Read(string brainId, CancellationToken ct)
            => throw new InvalidOperationException("source unavailable");
    }

    private sealed class CountingEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<List<string>> Batches { get; } = [];

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var batch = values.ToList();
            Batches.Add(batch);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(batch.Select(text =>
                new Embedding<float>(text.Length % 2 == 0 ? new float[] { 1, 0 } : [0, 1]))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}