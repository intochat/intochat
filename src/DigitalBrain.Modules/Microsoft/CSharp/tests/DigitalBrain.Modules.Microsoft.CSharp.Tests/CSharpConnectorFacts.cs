using System.Text.Json;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Files;
using DigitalBrain.Microsoft.CSharp.Files.Signals;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpConnectorFacts
{
    [Fact]
    public void TheConnectorNamesTheFileNeuronAndItsPublicSignal()
    {
        var connector = CSharpModule.Connector;
        Assert.Equal("csharp", connector.Id);
        Assert.Equal([typeof(ICSharpFile)], connector.Neurons);
        Assert.Equal([typeof(CSharpFileChanged)], connector.Signals);
        Assert.Empty(connector.RequiredIntegrations);
        Assert.Equal(["csharp"], connector.OptionalIntegrations);
    }

    [Fact]
    public async Task TheSandboxIsListedAsAConnector()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().WithHttpEdge().StartAsync(ct);
        var response = await brain.HttpClient.GetAsync("/connectors", ct);
        response.EnsureSuccessStatusCode();
        using var catalog = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var listed = Assert.Single(catalog.RootElement.EnumerateArray(), item => item.GetProperty("id").GetString() == "csharp");
        Assert.Equal(["ICSharpFile"], listed.GetProperty("neurons").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(["CSharpFileChanged"], listed.GetProperty("signals").EnumerateArray().Select(name => name.GetString()));
    }
}
