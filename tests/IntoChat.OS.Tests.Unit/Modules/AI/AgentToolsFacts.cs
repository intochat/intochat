using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.Runtime.Http.Surface;
using IntoChat.OS.Modules.AI.Agents.Tools;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Tests.Unit.Modules.AI;

public sealed class AgentToolsFacts
{
    private static readonly ToolSchema FocusPointSchema = ToolSchemas.Of(typeof(IFocus)).Single(static s => s.Name == "focus_point");
    private static readonly ToolSchema CodeOrchestrationRunSchema = ToolSchemas.Of(typeof(ICodeOrchestration)).Single(static s => s.Name == "code_orchestration_run_async");

    [Fact]
    public async Task ContractToolSendsNamedArgumentsAndTheDefaultKey()
    {
        (string C, string K, string M, string A)? seen = null;
        var tool = new ContractTool(FocusPointSchema, (c, k, m, a, _) =>
        {
            seen = (c, k, m, a);
            return Task.FromResult<JsonElement?>(null);
        });

        await tool.InvokeAsync(
            new AIFunctionArguments { ["target"] = "window:settings", ["tone"] = "Info", ["reason"] = "here" },
            TestContext.Current.CancellationToken);

        Assert.Equal((typeof(IFocus).FullName!, "{brain}/ino/focus", "Point"), (seen!.Value.C, seen.Value.K, seen.Value.M));
        using var args = JsonDocument.Parse(seen.Value.A);
        Assert.Equal("here", args.RootElement.GetProperty("reason").GetString());
        Assert.Equal("window:settings", args.RootElement.GetProperty("target").GetString());
        Assert.Equal("Info", args.RootElement.GetProperty("tone").GetString());
    }

    [Fact]
    public async Task AKeyedToolPrefixesTheBrain()
    {
        (string C, string K, string M, string A)? seen = null;
        var tool = new ContractTool(CodeOrchestrationRunSchema, (c, k, m, a, _) =>
        {
            seen = (c, k, m, a);
            return Task.FromResult<JsonElement?>(null);
        });

        await tool.InvokeAsync(
            new AIFunctionArguments { ["key"] = "code-orchestration/x1", ["title"] = "sum", ["goal"] = "sum numbers", ["preauthoredScript"] = "code" },
            TestContext.Current.CancellationToken);

        Assert.Equal((typeof(ICodeOrchestration).FullName!, "{brain}/code-orchestration/x1", "RunAsync"), (seen!.Value.C, seen.Value.K, seen.Value.M));
        using var args = JsonDocument.Parse(seen.Value.A);
        Assert.False(args.RootElement.TryGetProperty("key", out _));
        Assert.Equal("sum", args.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void MetaExposesOnlySearchListAndContext()
    {
        var meta = AgentTools.Meta(null, new NeuronSurface(), "conv-1");
        Assert.Equal(["neurons_search", "neurons_list", "neurons_context"], meta.Select(static t => t.Name));
    }
}
