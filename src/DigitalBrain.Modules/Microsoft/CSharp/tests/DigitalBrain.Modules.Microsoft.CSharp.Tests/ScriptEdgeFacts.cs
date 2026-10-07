using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Client;
using DigitalBrain.Client.Edge.V1;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class ScriptEdgeFacts
{
    [Fact]
    public async Task SameBrainScriptsCannotReplaceOrRestartAnotherAppsBoundBehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Brain(new FakeSandbox(), ct);
        CallerContextStamper.Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        var host = brain.Get<IAppBindingTestHost>("victim-app");
        await host.Bind("predictable-behavior");
        await host.Start("predictable-behavior");
        var file = brain.Get<ICSharpFile>("predictable-behavior");
        CallerContextStamper.Stamp(Alice with { Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy, AppId = "victim-app" });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => file.Write("malicious source", ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => file.Configure(new Dictionary<string, string>(), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => file.Start(ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => file.Stop(ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => file.Delete(ct));
        Orleans.Runtime.RequestContext.Clear();
    }

    [Fact]
    public async Task APreviousStandaloneTokenCannotSpeakForANewlyBoundSubscribedBehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var oldToken = await StartAsAlice(brain, sandbox, "reused-file", ct);
        CallerContextStamper.Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        await brain.Get<IAppBindingTestHost>("bound-app").Bind("reused-file");
        Orleans.Runtime.RequestContext.Clear();
        CallerContextStamper.Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        await brain.Get<IAppBindingTestHost>("bound-app").Start("reused-file");
        Orleans.Runtime.RequestContext.Clear();
        var currentToken = sandbox.Requests.Last(FakeSandbox.IsStart).Body!["environment"]![DigitalBrainConfiguration.EnvironmentName(ScriptEdgeProtocol.TokenSetting)]!.GetValue<string>();
        var run = brain.SiloServices.GetRequiredService<RunTokens>().Validate(currentToken)!;
        await brain.Get<ICSharpFileEdge>("reused-file").Subscribed(run.Run, brain.Get<IPinger>("pinger").GetGrainId().ToString(), nameof(Pinged));
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => edge.InvokeAsync(oldToken, Call("Caller"), ct));
        Assert.Equal("alice App AppProxy bound-app", (await edge.InvokeAsync(currentToken, Call("Caller"), ct))!.Value.GetString());
    }

    [Fact]
    public async Task BehaviorsShareTheVerifiedAppIdentityWhileSettingsAndInheritedCallersCannotBindIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        CallerContextStamper.Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        var host = brain.Get<IAppBindingTestHost>("stable-app");
        await host.Bind("behavior-one");
        await host.Bind("behavior-two");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Bind("behavior-three", "someone-else"));
        Orleans.Runtime.RequestContext.Clear();
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        foreach (var file in new[] { "behavior-one", "behavior-two" })
        {
            CallerContextStamper.Stamp(Alice);
            await brain.AuthorizeCallerAsync();
            await host.Start(file);
            Orleans.Runtime.RequestContext.Clear();
            var token = sandbox.Requests.Last(FakeSandbox.IsStart).Body!["environment"]![DigitalBrainConfiguration.EnvironmentName(ScriptEdgeProtocol.TokenSetting)]!.GetValue<string>();
            Assert.Equal("alice App AppProxy stable-app", (await edge.InvokeAsync(token, Call("Caller"), ct))!.Value.GetString());
        }
        var standalone = brain.Get<ICSharpFile>("standalone");
        await standalone.Configure(new Dictionary<string, string> { ["App"] = "someone-else" }, ct);
        var standaloneToken = await StartAsAlice(brain, sandbox, "standalone", ct);
        Assert.Equal("alice App AppProxy standalone", (await edge.InvokeAsync(standaloneToken, Call("Caller"), ct))!.Value.GetString());
    }

    [Fact]
    public async Task CapacityRefusalTravelsThroughTheScriptHttpClientVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var token = await StartAsAlice(brain, sandbox, "workspace-a/refusal", ct);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(brain.SiloServices.GetRequiredService<ScriptEdge>());
        await using var host = builder.Build();
        host.MapScriptEdge();
        await host.StartAsync(ct);
        await using var client = await DigitalBrainClient.ConnectAsync(["--" + ScriptEdgeProtocol.EdgeSetting + "=" + host.Urls.Single(), "--" + ScriptEdgeProtocol.TokenSetting + "=" + token], ct);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.Get<IPinger>("pinger").RefuseCapacity());
        Assert.Equal(DigitalBrain.Framework.Capacity.CapacityUnavailableException.RefusalMessage, error.Message);
        await host.StopAsync(ct);
    }

    private static readonly CallerContext Alice = new()
    {
        PrincipalId = "alice",
        AccountId = "account-a",
        BrainId = "workspace-a",
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    };

    [Fact]
    public async Task ARunCallsNeuronsAsItsFileOwnerStampedAsAnApp()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var token = await StartAsAlice(brain, sandbox, "workspace-a/report", ct);
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();

        var doubled = await edge.InvokeAsync(token, Call("Double", 21), ct);
        var caller = await edge.InvokeAsync(token, Call("Caller"), ct);

        Assert.Equal(42, doubled!.Value.GetInt32());
        Assert.Equal("alice App AppProxy workspace-a/report", caller!.Value.GetString());
    }

    [Fact]
    public async Task TokensStopSpeakingForAFileOnceItStops()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var token = await StartAsAlice(brain, sandbox, "workspace-a/stopped", ct);
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();

        await brain.Get<ICSharpFile>("workspace-a/stopped").Stop(ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => edge.InvokeAsync(token, Call("Double", 1), ct));
    }

    [Fact]
    public async Task RefusesForgedTokensAndContractsOutsideTheInstalledOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var token = await StartAsAlice(brain, sandbox, "workspace-a/forged", ct);
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => edge.InvokeAsync(token[..^2] + "AA", Call("Double", 1), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => edge.InvokeAsync(null, Call("Double", 1), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => edge.InvokeAsync(token, (typeof(ICSharpFile).FullName!, "pinger", "Double", [JsonSerializer.SerializeToElement(1)]), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => edge.InvokeAsync(token, Call("Watch", 1), ct));
    }

    [Fact]
    public async Task SignalsReachTheScriptAsJsonOfTheRequestedTypeOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var token = await StartAsAlice(brain, sandbox, "workspace-a/listener", ct);
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var signals = await edge.OpenSignalsAsync(token, typeof(IPinger).FullName!, "pinger", nameof(Pinged), stop.Token);
        await brain.Get<IPinger>("pinger").Ping(5);

        await foreach (var signal in signals)
        {
            Assert.Equal(5, SignalJson.Number(signal));
            break;
        }
        await stop.CancelAsync();
    }

    [Fact]
    public async Task TheModuleAllowsTheComposedModulesContractAssembliesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await SandboxBrain.StartAsync(new FakeSandbox(), ct);

        var contracts = brain.SiloServices.GetRequiredService<ScriptContracts>();

        Assert.Equal(typeof(ICSharpFile), contracts.Find(typeof(ICSharpFile).FullName!));
        // Aspire's contracts are deployed beside this host, but its module is not composed here.
        Assert.Throws<ArgumentException>(() => contracts.Find(typeof(DigitalBrain.Microsoft.Aspire.IAspire).FullName!));
    }

    [Fact]
    public void PlatformOnlyContractsAreNotInTheScriptCatalog()
    {
        var contracts = new ScriptContracts([typeof(IPinger).Assembly, typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).Assembly]);

        Assert.Equal(typeof(IPinger), contracts.Find(typeof(IPinger).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Find(typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Find(typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Find(typeof(IPlatformOnlyPinger).FullName!));
    }

    [Fact]
    public void TheWholePlatformAssemblyContributesNoScriptContractsEvenWithoutAttributesOnTheTypes()
    {
        var platform = typeof(DigitalBrain.Platform.PlatformHosting).Assembly;
        Assert.True(DigitalBrain.Framework.PlatformAssemblyAttribute.IsPlatform(platform));
        var contracts = new ScriptContracts([platform]);

        foreach (var neuronContract in platform.GetExportedTypes().Where(type => type.IsInterface && typeof(DigitalBrain.INeuron).IsAssignableFrom(type)))
        {
            Assert.Throws<ArgumentException>(() => contracts.Find(neuronContract.FullName!));
            Assert.True(DigitalBrain.Framework.PlatformOnlyAttribute.AppliesTo(neuronContract));
        }
    }

    [Fact]
    public void ScriptAccountsAreVisibleAndTheCredentialRegistryIsNot()
    {
        var contracts = new ScriptContracts([
            typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly,
            typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).Assembly,
        ]);

        Assert.Equal(
            typeof(DigitalBrain.Integrations.IConnectionRequests),
            contracts.Find(typeof(DigitalBrain.Integrations.IConnectionRequests).FullName!));
        Assert.Throws<ArgumentException>(() => contracts.Find(typeof(DigitalBrain.Framework.Platform.Connections.IConnectionIndex).FullName!));
    }

    [Fact]
    public void ThePlatformAssemblyIsNeverAContractsAssemblyOfAComposedModule()
    {
        var inventory = new DigitalBrain.Framework.ModuleInventory([typeof(DigitalBrain.Platform.PlatformHosting)]);

        Assert.DoesNotContain(typeof(DigitalBrain.Platform.PlatformHosting).Assembly, inventory.ContractAssemblies());
        Assert.Empty(DigitalBrain.Framework.ModuleInventory.ContractAssembliesOf(typeof(DigitalBrain.Platform.PlatformHosting).Assembly));
    }

    [Fact]
    public void TheIntegrationRegistrationContractIsAbsentFromTheScriptCatalogBecauseReleaseReliesOnThat()
    {
        var contracts = new ScriptContracts([typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).Assembly]);

        Assert.True(DigitalBrain.Framework.PlatformOnlyAttribute.AppliesTo(typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration)));
        Assert.Throws<ArgumentException>(() => contracts.Find(typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName!));
    }

    [Fact]
    public async Task TheEdgeRefusesToInvokeAPlatformOnlyContractWithAForgedPlatformCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct, silo => silo.Services.AddSingleton(
            new ScriptContracts([typeof(IPinger).Assembly, typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).Assembly])));
        var token = await StartAsAlice(brain, sandbox, "workspace-a/attacker", ct);
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        var forged = JsonSerializer.SerializeToElement(new
        {
            principalId = "x",
            accountId = "x",
            brainId = "x",
            kind = "Platform",
            stampedBy = "Platform",
            appId = "x",
        });

        await Assert.ThrowsAsync<ArgumentException>(() => edge.InvokeAsync(token,
            (typeof(DigitalBrain.Framework.Platform.Integrations.IIntegration).FullName!, "integration/openai", "Release", [forged]), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => edge.InvokeAsync(token,
            (typeof(DigitalBrain.Framework.Platform.Secrets.ISecrets).FullName!, "owner", "Resolve", [forged, forged]), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => edge.OpenSignalsAsync(token, typeof(IPlatformOnlyPinger).FullName!, "x", "Pinged", ct));
    }

    [Fact]
    public async Task SignalsPublishedBetweenRunsArriveWhenTheNextRunSubscribes()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        var token = await StartAsAlice(brain, sandbox, "workspace-a/durable", ct);

        // The first run subscribes, which registers durably, then dies with its stream.
        using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await edge.OpenSignalsAsync(token, typeof(IPinger).FullName!, "pinger", nameof(Pinged), first.Token);
        await first.CancelAsync();
        sandbox.ExitLatest(0);

        await pinger.Ping(41);                        // nobody is streaming: buffered, wakes a run
        await Eventually(() => sandbox.Started == 2, ct);
        var next = LatestToken(sandbox);              // the wake-run's token

        var resumed = await edge.OpenSignalsAsync(next, typeof(IPinger).FullName!, "pinger", nameof(Pinged), ct);
        await foreach (var json in resumed.WithCancellation(ct))
        {
            Assert.Contains("\"number\":41", json, StringComparison.Ordinal);   // the buffered signal came first
            break;
        }
    }

    [Fact]
    public async Task ALiveStreamReceivesASignalPromptlyThroughTheDoorbell()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var pinger = brain.Get<IPinger>("pinger");
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        var token = await StartAsAlice(brain, sandbox, "workspace-a/live", ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var stream = await edge.OpenSignalsAsync(token, typeof(IPinger).FullName!, "pinger", nameof(Pinged), stop.Token);
        var reading = ReadOneAsync(stream, stop.Token);
        await pinger.Ping(7);

        Assert.Contains("\"number\":7", await reading.WaitAsync(TimeSpan.FromSeconds(5), ct), StringComparison.Ordinal);
        await stop.CancelAsync();
    }

    [Fact]
    public async Task StreamsRefuseTokensThatNoLongerSpeakForTheFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        var token = await StartAsAlice(brain, sandbox, "workspace-a/revoked", ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await edge.OpenSignalsAsync(token, typeof(IPinger).FullName!, "pinger", nameof(Pinged), stop.Token);
        await stop.CancelAsync();

        await brain.Get<ICSharpFile>("workspace-a/revoked").Stop(ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => edge.OpenSignalsAsync(token, typeof(IPinger).FullName!, "pinger", nameof(Pinged), ct));
    }

    private static async Task<string> ReadOneAsync(IAsyncEnumerable<string> stream, CancellationToken ct)
    {
        await foreach (var json in stream.WithCancellation(ct)) { return json; }
        throw new InvalidOperationException("The stream ended without a signal.");
    }

    private static string LatestToken(FakeSandbox sandbox)
        => sandbox.Requests.Last(FakeSandbox.IsStart).Body!["environment"]![DigitalBrainConfiguration.EnvironmentName(ScriptEdgeProtocol.TokenSetting)]!.GetValue<string>();

    private static async Task Eventually(Func<bool> condition, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) { await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token); }
    }

    private static (string Contract, string Key, string Method, JsonElement[] Arguments) Call(string method, params int[] arguments)
        => (typeof(IPinger).FullName!, "pinger", method, [.. arguments.Select(argument => JsonSerializer.SerializeToElement(argument))]);

    private static async Task<string> StartAsAlice(ModuleBrain brain, FakeSandbox sandbox, string fileId, CancellationToken ct)
    {
        var file = brain.Get<ICSharpFile>(fileId);
        CallerContextStamper.Stamp(Alice);
        await brain.AuthorizeCallerAsync();
        await brain.AuthorizeCallerAsync();
        await file.Write("Console.WriteLine(1);", ct);
        try { await file.Start(ct); }
        finally { Orleans.Runtime.RequestContext.Clear(); }
        return sandbox.Requests.Last(FakeSandbox.IsStart).Body!["environment"]![DigitalBrainConfiguration.EnvironmentName(ScriptEdgeProtocol.TokenSetting)]!.GetValue<string>();
    }

    [Fact]
    public async Task ABatchWhoseEdgeDiedMidwayIsRedeliveredAfterReconnectUntilItIsAcknowledged()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await Brain(sandbox, ct);
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        const string fileId = "workspace-a/redelivery";
        var token = await StartAsAlice(brain, sandbox, fileId, ct);
        var file = brain.Get<ICSharpFile>(fileId);
        var pinger = brain.Get<IPinger>("pinger");
        var contract = typeof(IPinger).FullName!;

        using var dying = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var firstConnection = await edge.OpenSignalsAsync(token, contract, "pinger", nameof(Pinged), dying.Token);
        await using var arrivals = await brain.Observe<CSharpPendingArrived>(file, ct);
        await pinger.Ping(1);
        await pinger.Ping(2);
        await arrivals.NextAsync(ct: ct);
        await arrivals.NextAsync(ct: ct);
        await using (var first = firstConnection.GetAsyncEnumerator(dying.Token))
        {
            // The connection dies after one signal of a two-signal batch was handed on.
            Assert.Equal([1], (await ReadAsync(first, 1)).Select(SignalJson.Number));
        }
        await dying.CancelAsync();
        Assert.Equal(2, Assert.Single((await file.Inspect(ct)).Subscriptions).Pending);

        using var live = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var secondConnection = await edge.OpenSignalsAsync(token, contract, "pinger", nameof(Pinged), live.Token);
        await using var second = secondConnection.GetAsyncEnumerator(live.Token);
        Assert.Equal([1, 2], (await ReadAsync(second, 2)).Select(SignalJson.Number));

        // Asking for more is what hands the batch over: the edge acknowledges, publishing CSharpFileChanged.
        await using var changes = await brain.Observe<CSharpFileChanged>(file, ct);
        var more = second.MoveNextAsync().AsTask();
        await changes.NextAsync(ct: ct);
        var view = Assert.Single((await file.Inspect(ct)).Subscriptions);
        Assert.Equal((0, 2L), (view.Pending, view.Delivered));
        await live.CancelAsync();
        try { await more; } catch (OperationCanceledException) { }
    }

    private static async Task<string[]> ReadAsync(IAsyncEnumerator<string> stream, int count)
    {
        var items = new List<string>();
        while (items.Count < count && await stream.MoveNextAsync()) { items.Add(stream.Current); }
        return [.. items];
    }

    [Fact]
    public async Task AScriptCanCallInspectOnTheCSharpFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var sandbox = new FakeSandbox();
        await using var brain = await SandboxBrain.StartAsync(sandbox, ct, silo => silo.Services.AddSingleton(new ScriptContracts([typeof(IPinger).Assembly, typeof(ICSharpFile).Assembly])));
        const string fileId = "workspace-a/files/edge-inspect";
        var token = await StartAsAlice(brain, sandbox, fileId, ct);
        var edge = brain.SiloServices.GetRequiredService<ScriptEdge>();
        var inspected = await edge.InvokeAsync(token, (typeof(ICSharpFile).FullName!, fileId, nameof(ICSharpFile.Inspect), []), ct);
        Assert.True(inspected!.Value.TryGetProperty("status", out _));
    }

    [Fact]
    public void ThePlatformContractsAssemblyIsInEveryInventory()
    {
        var inventory = new DigitalBrain.Framework.ModuleInventory([]);
        Assert.Contains(typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly, inventory.ContractAssemblies());
        Assert.NotEqual(typeof(DigitalBrain.INeuron).Assembly, typeof(DigitalBrain.Integrations.IConnectionRequests).Assembly);
    }

    // The test's own IPinger stands in for an installed module contract.
    private static Task<ModuleBrain> Brain(FakeSandbox sandbox, CancellationToken ct)
        => SandboxBrain.StartAsync(sandbox, ct, silo => silo.Services.AddSingleton(new ScriptContracts([typeof(IPinger).Assembly])));
}
