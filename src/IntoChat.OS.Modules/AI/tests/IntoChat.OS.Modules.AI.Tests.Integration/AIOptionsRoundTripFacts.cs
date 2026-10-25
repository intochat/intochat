using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class AIOptionsRoundTripFacts
{
    [Trait("host", "own")]
    [Fact]
    public void PopulatedDictionariesSurviveCompileAndBindByValue()
    {
        var options = new AIOptions();
        options.ModelProfiles["Fast"] = new AIModelProfileOptions { Provider = "OpenAI", Model = "m1", ContextWindowTokens = 1000 };
        options.Ollama.Models["Gemma"] = new AIModelOptions { Model = "gemma:2b" };
        options.OpenRouter.Endpoint = "https://openrouter.ai/api/v1";
        var definition = ModuleOptionsSerialization.Compile<AIModule, AIOptions>(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(definition.Settings).Build();

        var bound = configuration.GetModuleOptions<AIOptions>("ai");

        Assert.Equal("m1", bound.ModelProfiles["fast"].Model);
        Assert.Equal(1000, bound.ModelProfiles["Fast"].ContextWindowTokens);
        Assert.Equal("gemma:2b", bound.Ollama.Models["gemma"].Model);
        Assert.Equal(options.OpenRouter.Endpoint, bound.Provider(AiProvider.OpenRouter).Endpoint);
    }
}
