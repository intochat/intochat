using DigitalBrain;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using IntoChat.Muncly;
using IntoChat.Muncly.Screens;
using IntoChat.OS.Modules.Mcp;
using IntoChat.OS.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Unit.Components.Muncly;

public sealed class MunclyAppFacts
{
    [Fact]
    public void StateEnforcesHttpsUrlAndSafeDeepLinks()
    {
        var initial = new MunclyAppState();
        Assert.True(initial.ValidBaseUrlInvariant);
        Assert.True(initial.ValidPathInvariant);
        Assert.Equal("https://crm.munc.ly", initial.CurrentUrl);

        var connected = initial.ConnectEndpoint("https://acme.munc.ly/dashboard");
        Assert.True(connected.IsAccepted);
        Assert.Equal("https://acme.munc.ly", connected.Next!.BaseUrl);

        var opened = connected.Next.OpenPath(null, objectName: "account", recordId: "acc-42");
        Assert.True(opened.IsAccepted);
        Assert.Equal("https://acme.munc.ly/objects/account/acc-42", opened.Next!.CurrentUrl);

        var bad = initial.ConnectEndpoint("http://evil.example.com");
        Assert.False(bad.IsAccepted);

        var creds = initial.ConnectEndpoint("https://user:pass@acme.munc.ly");
        Assert.False(creds.IsAccepted);

        var loopback = initial.ConnectEndpoint("http://localhost:8080");
        Assert.True(loopback.IsAccepted);
    }

    [Fact]
    public void OptionsBuildMcpEndpointWithOptionalToolSetsAndValidate()
    {
        var opts = new MunclyOptions
        {
            BaseUrl = "https://acme.munc.ly/",
            ToolSets = "core,setup",
            AllowedTools = ["list_records", "describe_object"],
        };
        opts.Validate();
        Assert.Equal("https://acme.munc.ly/api/mcp?sets=core%2Csetup", opts.BuildMcpEndpointUrl());

        var badUrl = new MunclyOptions { BaseUrl = "http://not-https.example.com" };
        Assert.Throws<ArgumentException>(badUrl.Validate);

        var badTools = new MunclyOptions { BaseUrl = "https://acme.munc.ly", AllowedTools = [""] };
        Assert.Throws<ArgumentException>(badTools.Validate);
    }

    [Fact]
    public void McpModuleAutoAttachesMunclyServerFromBaseUrlAndToken()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Modules:muncly:Options:BaseUrl"] = "https://acme.munc.ly",
                ["IntoChatOS:Modules:muncly:Options:ToolSets"] = "core,setup",
                ["IntoChatOS:Integrations:muncly:McpToken"] = "tok_live_123",
            })
            .Build();

        var mcp = new McpModuleOptions();
        McpModule.AttachMunclyServer(mcp, config);

        Assert.True(mcp.Servers.TryGetValue("muncly-crm", out var server));
        Assert.Equal("https://acme.munc.ly/api/mcp?sets=core%2Csetup", server!.Url);
        Assert.Equal("Muncly CRM", server.Title);
        Assert.Equal("Authorization", server.HeaderName);
        Assert.Equal("Bearer tok_live_123", server.HeaderValue);
        mcp.Validate();
    }

    [Fact]
    public async Task MunclyAppHydratesConfiguredBaseUrlAndRendersEmbeddedWebBrowser()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [MunclyAppState.ConfiguredUrlKey] = "https://acme.munc.ly",
            })
            .Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .BuildServiceProvider();

        await using var os = TestOS.Local(o => o.Services = services);
        os.Register<MunclyApp>();

        var brain = new BrainId("facts");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("facts", "facts", brain));
        var screen = os.Get<IScreen>(new NeuronId(brain, "app.muncly", "default"));
        var opened = await screen.Open();

        Assert.NotNull(opened);
        Assert.Contains("https://acme.munc.ly", opened!.View.GetRawText(), StringComparison.Ordinal);
    }
}
