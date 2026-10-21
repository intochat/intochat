extern alias AppHost;
using AppHost::IntoChat.OS.AppHost;
using Aspire.Hosting.Testing;
using IntoChat.OS.AI;
using IntoChat.OS.Aspire.Hosting;

namespace IntoChat.OS.Kernel.Tests.Unit;

[Collection(AppHostComposition.Collection)]
public sealed class RunProfileFacts
{
    [Fact]
    public void LocalAndCloudAllocateDistinctOptionGraphs()
    {
        var first = RunProfile.Local;
        var second = RunProfile.Local;
        Assert.NotSame(first.Ai, second.Ai);
        first.Ai.Default.Model = "mutated";
        Assert.Equal("IQwen35", RunProfile.Local.Ai.Default.Model);
        Assert.Equal("IDeepSeekV41Flash", RunProfile.Cloud.Ai.Default.Model);
        Assert.Null(RunProfile.Cloud.Ai.Default.SystemOne);
        Assert.Equal("ILocalJev", RunProfile.Local.Ai.Default.SystemOne);
        Assert.False(RunProfile.Local.Ai.Tavily.Enabled);
        Assert.True(RunProfile.Cloud.Ai.Tavily.Enabled);
        Assert.Equal("http://127.0.0.1:1/v1/", RunProfile.Reference.Ai.OpenAI.Endpoint);
        Assert.False(RunProfile.Reference.Ai.Tavily.Enabled);
        Assert.Equal("IGpt56Luna", RunProfile.Reference.Ai.Default.Model);
    }

    [Theory]
    [InlineData("local", true)]
    [InlineData("dev+local", true)]
    [InlineData("local+dev", true)]
    [InlineData("LOCAL", true)]
    [InlineData("dev", false)]
    [InlineData("", false)]
    public void TheProfileNamesSelectTheLeanBrainOrTheCloudBrain(string profile, bool lean)
    {
        var selected = RunProfile.ForName(IntoChatOSProfiles.Parse(profile));
        Assert.Equal(lean, selected.Lean);
        Assert.Equal(lean ? "IQwen35" : "IDeepSeekV41Flash", selected.Ai.Default.Model);
    }

    [Fact]
    public void ALeanBrainSkipsEveryHostingTheLocalRunDoesNotNeed()
    {
        Assert.True(RunProfile.LocalLean.Lean);
        Assert.False(RunProfile.Cloud.Lean);
        Assert.False(RunProfile.Local.Lean);
    }

    [Fact]
    public async Task TheProductEntryPointAppliesCloudAndKeepsTheMasterKey()
    {
        await using var appHost = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.IntoChat_OS_AppHost>(
            ["Parameters:Modules-intochat-os-master-key=synthetic-composition-test-key-32chars"], TestContext.Current.CancellationToken);
        Assert.Contains(appHost.Resources, resource => resource.Name == "Modules-intochat-os-master-key");
        Assert.DoesNotContain(appHost.Resources, resource => resource.Name.Contains("gmail-client", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(appHost.Resources, resource => resource.Name.Contains("salesforce-consumer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheReferenceOverlayCarriesRunProfileReference()
    {
        var overlaid = new AIOptions();
        ReferenceBrain.ApplyReferenceAi(overlaid);
        var reference = RunProfile.Reference.Ai;
        Assert.Equal(reference.Default.Model, overlaid.Default.Model);
        Assert.Equal(reference.Default.Embedding, overlaid.Default.Embedding);
        Assert.Equal(reference.Default.Transcription, overlaid.Default.Transcription);
        Assert.Equal(reference.Default.SystemOne, overlaid.Default.SystemOne);
        Assert.Equal(reference.Tavily.Enabled, overlaid.Tavily.Enabled);
        Assert.Equal(reference.OpenAI.Endpoint, overlaid.OpenAI.Endpoint);
        Assert.Equal(reference.Hosting.Llms, overlaid.Hosting.Llms);
        Assert.Equal(reference.Hosting.Embeddings, overlaid.Hosting.Embeddings);
        Assert.Equal(reference.Hosting.SystemOnes, overlaid.Hosting.SystemOnes);
    }
}
