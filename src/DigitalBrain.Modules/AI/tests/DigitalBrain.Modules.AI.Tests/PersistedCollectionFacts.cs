using System.Text.Json;
using DigitalBrain.AI;
using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Inference;
using Xunit;

namespace DigitalBrain.Modules.AI.Tests;

public sealed class PersistedCollectionFacts
{
    [Fact]
    public void AgentDefinitionCollectionsAndNullableOptionsRoundTripThroughJson()
    {
        var definition = new AgentDefinition
        {
            Contracts = ["DigitalBrain.Apps.IApp"],
            Capabilities = ["research"],
            RoutingExamples = ["find evidence"],
            Contexts = ["research/agents/main/context"],
            Options = new(ProviderOptions: new Dictionary<string, string>() { ["mode"] = "precise" }),
        };
        var restored = JsonSerializer.Deserialize<AgentDefinition>(JsonSerializer.Serialize(definition))!;
        Assert.Equal(definition.Contracts, restored.Contracts);
        Assert.Equal(definition.Capabilities, restored.Capabilities);
        Assert.Equal(definition.RoutingExamples, restored.RoutingExamples);
        Assert.Equal(definition.Contexts, restored.Contexts);
        Assert.Equal("precise", restored.Options!.ProviderOptions!["mode"]);
        Assert.Null(JsonSerializer.Deserialize<InferenceOptions>("{}")!.ProviderOptions);
        Assert.Null(JsonSerializer.Deserialize<InferenceOptions>("{\"ProviderOptions\":null}")!.ProviderOptions);
        Assert.Empty(JsonSerializer.Deserialize<InferenceOptions>("{\"ProviderOptions\":{}}")!.ProviderOptions!);
    }
}
