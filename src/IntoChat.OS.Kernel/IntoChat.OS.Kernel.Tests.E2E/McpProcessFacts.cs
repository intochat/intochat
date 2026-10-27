using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Modules.Flutter.WebBrowser;
using IntoChat.OS.Modules.Microsoft.CSharp;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Tests.E2E;

// The real MCP executable carries contracts, while the silo hosts implementations in another process.
// An MCP edge hosted inside a test process also sees the silo's classes and cannot detect client routing regressions.
[Trait("host", "own")]
public sealed class McpProcessFacts
{
    [Fact(Timeout = 180_000)]
    public async Task AContractsOnlyMcpCallsEveryHostPolicyAndKeepsBrainAccessChecks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithModule<CSharpModule>()
            .WithModule<FlutterModule, FlutterModuleOptions>(options => options.BackendOnly()).WithModule<AIModule>().StartAsync(ct);
        var scope = brain.Owner.BrainId;
        var textKey = scope + "/mcp-display";
        await brain.Get<IText>(textKey).Set("four");

        var own = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var directory = Path.Combine(own.Parent!.Parent!.FullName, "IntoChat.OS.Mcp", own.Name);
        foreach (var assembly in new[] { "IntoChat.OS.Modules.Microsoft.CSharp", "IntoChat.OS.Modules.Flutter", "IntoChat.OS.Modules.AI" })
        { Assert.False(File.Exists(Path.Combine(directory, assembly + ".dll")), "The MCP must remain a contracts-only client of " + assembly); }
        var configuration = brain.Services.GetRequiredService<IConfiguration>();
        var gateway = configuration[TopologyContext.GatewayPortKey]
            ?? throw new InvalidOperationException("The local test host did not publish its gateway port.");
        await using var mcp = new ChildProcessHost(Path.Combine(directory, "IntoChat.OS.Mcp.dll"), new Dictionary<string, string>
        {
            ["Orleans:Clustering:ProviderType"] = "Development",
            ["Orleans:Clustering:Gateways:0"] = "127.0.0.1:" + gateway,
        });
        try
        {
            await mcp.StartAsync(TimeSpan.FromSeconds(60), ct);
            using var client = new HttpClient { BaseAddress = mcp.BaseAddress };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");

            var text = await Call<IText>(client, scope, textKey, nameof(IText.Read), "[]", ct);
            Assert.False(text.IsError, text.Text);
            Assert.Equal("four", JsonDocument.Parse(text.Text).RootElement.GetProperty("markdown").GetString());

            var runtime = await Call<IBrainRuntime>(client, scope, scope + "/runtime", nameof(IBrainRuntime.Inspect), "[]", ct);
            Assert.False(runtime.IsError, runtime.Text);
            Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(runtime.Text).RootElement.GetProperty("behaviors").ValueKind);

            // The runtime refuses a reentrant or stateless neuron reached through the affine host grain.
            var browser = await Call<IWebBrowser>(client, scope, scope + "/mcp-browser", nameof(IWebBrowser.Read), "[]", ct);
            Assert.False(browser.IsError, browser.Text);
            Assert.Equal(scope + "/mcp-browser", JsonDocument.Parse(browser.Text).RootElement.GetProperty("name").GetString());
            var models = await Call<IModelCatalog>(client, scope, IModelCatalog.Key(scope), nameof(IModelCatalog.List), "[null]", ct);
            Assert.False(models.IsError, models.Text);
            Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(models.Text).RootElement.GetProperty("models").ValueKind);

            // Selecting the owner's route never authorizes a raw neuron key in another brain.
            var denied = await Call<IText>(client, scope, "foreign/mcp-display", nameof(IText.Read), "[]", ct);
            Assert.True(denied.IsError);
            Assert.StartsWith("UnauthorizedAccessException:", denied.Text, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(mcp.LogPath)) { File.Copy(mcp.LogPath, Path.Combine(AppContext.BaseDirectory, "mcp-routing-child.log"), overwrite: true); }
        }
    }

    private static async Task<(string Text, bool IsError)> Call<T>(HttpClient client, string brain, string key, string method, string arguments, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/brains/" + brain + "/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "neurons_call", arguments = new { contract = typeof(T).FullName!, key, method, arguments } },
        }, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]);
        var result = document.RootElement.GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.TryGetProperty("isError", out var error) && error.GetBoolean());
    }
}
