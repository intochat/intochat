using System.Text.Json;
using DigitalBrain.Contracts;
using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class ToolRouterFacts
{
    private static IReadOnlyList<AIFunction> Fake(int count) =>
        [.. Enumerable.Range(1, count).Select(i =>
            (AIFunction)new ContractTool(
                new ToolSchema("Sample.IFake", $"fake_tool_{i}", $"Method{i}", $"Fake tool number {i}.", """{"type":"object","properties":{}}""", "fake/key", null, true),
                static (_, _, _, _, _) => Task.FromResult<JsonElement?>(null)))];

    private static IReadOnlyList<AIFunction> InoLikeTools()
    {
        Type[] contracts = [typeof(IFiles), typeof(IFocus), typeof(IData), typeof(IStudio), typeof(IUserSettings)];
        return
        [
            .. contracts.SelectMany(ToolSchemas.Of).Select(schema =>
                (AIFunction)new ContractTool(schema, static (_, _, _, _, _) => Task.FromResult<JsonElement?>(null))),
        ];
    }

    private sealed class ThrowingEmbedder : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Embedding service offline.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Fact]
    public async Task RouterReturnsAllWhenFew() =>
        Assert.Equal(10, (await new ToolRouter(Fake(10), null).Route("anything", k: 8, TestContext.Current.CancellationToken)).Count);

    [Fact]
    public async Task RouterPicksTheFileToolForAFileRequest()
    {
        var routed = await new ToolRouter(InoLikeTools(), null)
            .Route("write a new text file called groceries.txt", k: 4, TestContext.Current.CancellationToken);
        Assert.Contains(routed, static t => t.Name == "files_write");
    }

    [Fact]
    public async Task RouterSurvivesAThrowingEmbedder() =>
        Assert.Equal(4, (await new ToolRouter(Fake(30), new ThrowingEmbedder()).Route("x", k: 4, TestContext.Current.CancellationToken)).Count);
}
