using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS;
using IntoChat.OS.Apps;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Integration;

public sealed class SessionCapabilitiesFacts
{
    [Trait("host", "own")]
    [Theory]
    [InlineData(null, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DeveloperModeReflectsWhetherTheComposedSandboxCanRun(bool? canRun, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = TestBrain.Create().WithModule<AppsModule>();
        // Developer mode is the sandbox's own answer; the fake gives each answer without a script process.
        if (canRun.HasValue) { builder.ConfigureServices(services => services.AddSingleton<IScriptSandbox>(new Sandbox(canRun.Value))); }
        await using var brain = await builder.StartAsync(ct);

        var payload = await brain.HttpClient.GetFromJsonAsync<JsonElement>("/session/capabilities", ct);

        Assert.Equal(expected, payload.GetProperty("developerMode").GetBoolean());
    }

    private sealed class Sandbox(bool canRun) : IScriptSandbox
    {
        public Task<Verdict[]> Judge(string brain, IReadOnlyDictionary<string, string> bundle, Signal signal, CancellationToken cancellationToken)
            => Task.FromResult<Verdict[]>([]);
        public bool CanRun => canRun;
        public string AuthoringDescription => "External script process";
        public Task<ScriptContractCatalog> ReadContracts(IReadOnlyList<string> modules, CancellationToken cancellationToken)
            => Task.FromResult(new ScriptContractCatalog([], [], ""));
        public Task<ScriptCompilationCheck> Compile(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken) => Task.FromResult(new ScriptCompilationCheck(true, []));
    }
}
