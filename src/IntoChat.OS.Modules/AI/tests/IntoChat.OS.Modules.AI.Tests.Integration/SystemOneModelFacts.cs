using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.AI.Models.TypeSafe;
using IntoChat.OS.Modules.AI.SystemOne;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Sdk;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// System One is the fourth model role: declared by marker like an LLM, carried by configuration,
// resolved at bind. Two markers ship: the hosted TypeSafe endpoint and the local adapter container.
public sealed class SystemOneModelFacts
{
    [Fact]
    public void WithSystemOneRecordsTheMarkerUnderHosting()
    {
        var options = new AIOptions().WithSystemOne<IJev>();

        Assert.Equal(["IJev"], options.Hosting.SystemOnes);
        Assert.Null(options.Default.SystemOne);
    }

    [Fact]
    public void WithDefaultSystemOneRecordsTheMarkerAsTheDefault()
    {
        var options = new AIOptions().WithDefaultSystemOne<ILocalJev>();

        Assert.Equal(["ILocalJev"], options.Hosting.SystemOnes);
        Assert.Equal("ILocalJev", options.Default.SystemOne);
    }

    [Fact]
    public void WithSystemOneAddsEachMarkerOnce()
    {
        var options = new AIOptions().WithSystemOne<IJev>().WithDefaultSystemOne<IJev>();

        Assert.Equal(["IJev"], options.Hosting.SystemOnes);
    }

    [Fact]
    public void LocalJevIsLocalAndHostedJevIsNot()
    {
        Assert.True(SystemOneModel.FindByMarker(typeof(ILocalJev))!.IsLocal);
        Assert.False(SystemOneModel.FindByMarker(typeof(IJev))!.IsLocal);
        Assert.Equal(AiProvider.TypeSafe, SystemOneModel.FindByMarkerName("IJev")!.Provider);
        Assert.Equal("llama3.1", SystemOneModel.FindByMarkerName("ILocalJev")!.Id);
    }

    [Fact]
    public void WithoutLocalModelsDropsTheLocalSystemOne()
    {
        var options = new AIOptions().WithDefaultSystemOne<ILocalJev>().WithoutLocalModels();

        Assert.Empty(options.Hosting.SystemOnes);
        Assert.Null(options.Default.SystemOne);
    }

    [Trait("host", "own")]
    [Fact]
    public void TheDefaultSystemOneSurvivesCompileAndBind()
    {
        var options = new AIOptions().WithDefaultSystemOne<ILocalJev>();
        var definition = ModuleOptionsSerialization.Compile<AIModule, AIOptions>(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(definition.Settings).Build();

        var bound = configuration.GetModuleOptions<AIOptions>("ai");

        Assert.Equal("ILocalJev", bound.Default.SystemOne);
        Assert.Equal(["ILocalJev"], bound.Hosting.SystemOnes);
        Assert.Equal(AIKeys.Options + ":Default:SystemOne", AIKeys.DefaultSystemOne);
    }
}
