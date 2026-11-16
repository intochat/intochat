using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections.Broker;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Framework;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Ino;

public sealed class HelloWorldInteractiveAppFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions ViewJson = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    private sealed class LiveCreatorHelloFixture : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlutterModule), typeof(AIModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            AppsModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.AddJournalPayloads(typeof(IApp).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlutterModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.Flutter.Views.IView).Assembly);
            services.AddJournalPayloads(typeof(DigitalBrain.Signals.Activated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddSingleton<ICapabilityConnections>(new SynapseStudioFacts.EmptyCapabilityConnections());
            services.TryAddSingleton<ICallFilter, CallFilter>();
            services.TryAddSingleton(TimeProvider.System);
            services.TryAddSingleton(IntoChat.OS.Connections.Contracts.NeuronTypeSearch.Create);

            // Configure AIModule + a test-only IModelInference backed by OfflineUnitSynthesizer so
            // Creator exercises the real ModelCatalog -> IModelInference -> BuildFlow -> Silo deploy pipeline
            // without requiring a live cloud API key or external Ollama daemon in CI.
            AIModule.ConfigureServices(services, new ConfigurationBuilder().Build());
            services.RemoveAll<IModelInference>();
            services.AddSingleton<IModelInference>(new TestSynthesizingInference());
        }
    }

    private sealed class TestSynthesizingInference : IModelInference
    {
        public bool HasProfile(string name) => false;

        public Task<ModelDescriptor> Describe(
            ModelSelection? selection = null,
            Type? marker = null,
            CancellationToken cancellationToken = default,
            string? brainId = null)
            => Task.FromResult(new ModelDescriptor(
                "Host",
                "test-synthesizer",
                null,
                "1",
                CapabilitySupport.Supported,
                CapabilitySupport.Unknown,
                CapabilitySupport.Unknown));

        public async Task<InferenceResult> Generate(
            InferenceRequest request,
            Type? marker = null,
            CancellationToken cancellationToken = default,
            string? brainId = null)
        {
            if (request.Tools is { Count: > 0 })
            {
                return await OfflineUnitSynthesizer.GenerateAsync("test-synthesizer", request, cancellationToken).ConfigureAwait(false);
            }

            var system = string.Join("\n", request.Messages
                .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .SelectMany(m => m.Content)
                .OfType<AiText>()
                .Select(t => t.Text));
            var prompt = string.Join("\n", request.Messages
                .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .SelectMany(m => m.Content)
                .OfType<AiText>()
                .Select(t => t.Text));
            var reply = await OfflineUnitSynthesizer.CompleteAsync(system, prompt, cancellationToken).ConfigureAwait(false);
            return new InferenceResult([new AiMessage("assistant", [new AiText(reply)])], "stop", null, null, "test-synthesizer");
        }

        public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(
            InferenceRequest request,
            Type? marker = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
            string? brainId = null)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static IDisposable AsPerson(INeuronRuntime runtime)
        => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value));

    private static string LocateHelloWorldFolder()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "apps", "helloworld");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "HelloWorld.cs")))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate apps/helloworld directory.");
    }

    [Fact]
    public async Task InoMakeCreatesVerifiesInstallsAndTogglesHelloWorldAppOnRealSilo()
    {
        await using var fixture = new LiveCreatorHelloFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = AsPerson(runtime);

        var inoStudio = runtime.Get<IInoStudio>(runtime.Id("ino.studio", "ino/studio"));
        var flows = runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));
        var installs = runtime.Get<IInstalls>(new NeuronId(runtime.Brain, "synapse.installs", "default"));

        // 1. User asks Ino to build an interactive Hello World toggle app
        var makeResult = await inoStudio.Make("Create a Hello World app where clicking a button makes 'Hello, IntoChat OS!' appear");
        Assert.True(makeResult.Ok, makeResult.Text);
        var card = Assert.IsType<InoCard>(makeResult.Card);
        Assert.Contains(card.Id, makeResult.Text, StringComparison.Ordinal);
        Assert.Contains("Tasks board", makeResult.Text, StringComparison.Ordinal);
        if (string.Equals(card.Status, "running", StringComparison.Ordinal))
        {
            // Under parallel Silo CPU contention, BuildFlow can exceed InoStudioNeuron.MakePollBudget (15s) and return running.
            FlowView? settled = null;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                settled = await flows.Read(card.Id, Ct);
                if (settled is not null
                    && (settled.Status is FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled
                        || (settled.Status == FlowStatus.Waiting && settled.WaitingStep == "install" && settled.Subject is not null)))
                {
                    break;
                }
                await Task.Delay(100, Ct);
            }
            Assert.NotNull(settled);
            Assert.True(settled!.Status is FlowStatus.Waiting or FlowStatus.Completed,
                $"Expected hello.world flow to reach waiting/completed, got {settled.Status}: {settled.Detail}");
            Assert.Equal("hello.world", settled.Subject);
        }
        else
        {
            Assert.Contains("hello.world", makeResult.Text, StringComparison.Ordinal);
            Assert.Contains($"/brains/{runtime.Brain.Value}/apps/hello.world/view", makeResult.Text, StringComparison.Ordinal);
        }

        // 2. Unit has zero external connector needs; approve its UI view/reaction install consent as Person
        var pending = Assert.Single(await installs.Pending(Ct), p => p.Alias == "hello.world");
        Assert.Empty(pending.Bindings);
        await runtime.ApproveAsPerson(pending.PlanId, Ct);

        FlowView? completed = null;
        for (var i = 0; i < 200; i++)
        {
            completed = await flows.Read(card.Id, Ct);
            if (completed?.Status == FlowStatus.Completed) { break; }
            await Task.Delay(50, Ct);
        }
        Assert.NotNull(completed);
        Assert.Equal(FlowStatus.Completed, completed!.Status);

        // 3. Initial live view hides "Hello, IntoChat OS!" and renders button "toggle-hello" ("Say Hello")
        var reader = runtime.Get<IViewReader>(new NeuronId(runtime.Brain, "hello.world", "default"));
        var v1 = await WaitForViewGenerationAsync(reader, "hello.world", minGeneration: 1);
        var json1 = JsonSerializer.Serialize(v1, ViewJson);
        Assert.DoesNotContain("Hello, IntoChat OS!", json1, StringComparison.Ordinal);
        Assert.Contains("Click the button below", json1, StringComparison.Ordinal);
        Assert.Contains("toggle-hello", json1, StringComparison.Ordinal);
        Assert.Contains("Say Hello", json1, StringComparison.Ordinal);

        // 4. Click "toggle-hello" -> "Hello, IntoChat OS!" appears
        await reader.Activate("hello.world", v1.Generation, "toggle-hello", null, "person");
        var v2 = await WaitForViewGenerationAsync(reader, "hello.world", minGeneration: v1.Generation + 1);
        var json2 = JsonSerializer.Serialize(v2, ViewJson);
        Assert.Contains("Hello, IntoChat OS!", json2, StringComparison.Ordinal);
        Assert.Contains("Hide Hello", json2, StringComparison.Ordinal);

        // 5. Click "toggle-hello" again -> "Hello, IntoChat OS!" hides
        await reader.Activate("hello.world", v2.Generation, "toggle-hello", null, "person");
        var v3 = await WaitForViewGenerationAsync(reader, "hello.world", minGeneration: v2.Generation + 1);
        var json3 = JsonSerializer.Serialize(v3, ViewJson);
        Assert.DoesNotContain("Hello, IntoChat OS!", json3, StringComparison.Ordinal);
        Assert.Contains("Click the button below", json3, StringComparison.Ordinal);
        Assert.Contains("Say Hello", json3, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportExportAndSkillDiscoveryWorkEndToEndForReferenceHelloWorldOnRealSilo()
    {
        await using var fixture = new LiveCreatorHelloFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        using var person = AsPerson(runtime);

        var studio = runtime.Get<ISynapseStudio>(ISynapseStudio.Key(runtime.Brain.Value));
        var inoStudio = runtime.Get<IInoStudio>(runtime.Id("ino.studio", "ino/studio"));
        var installs = runtime.Get<IInstalls>(new NeuronId(runtime.Brain, "synapse.installs", "default"));

        // 1. Import apps/helloworld via AppPackageArchive
        var imported = await AppPackageArchive.ImportFromDirectoryAsync(LocateHelloWorldFolder(), studio, services: runtime.Services, ct: Ct);
        Assert.True(imported.Succeeded, string.Join("\n", imported.Errors));
        Assert.Equal("helloworld", imported.Alias);

        // 2. SkillDiscovery + IInoStudio.List() surface deployed SKILL.md sidecar
        var skills = await SkillDiscovery.DiscoverAsync(studio, ct: Ct);
        var helloSkill = Assert.Single(skills, s => s.Alias == "helloworld");
        Assert.Equal("deployed-unit", helloSkill.Source);
        var listResult = await inoStudio.List();
        Assert.Contains("[skill] helloworld", listResult.Text, StringComparison.Ordinal);

        // 3. Export deployed unit to ZIP and verify all 4 files round-trip
        var zipBytes = await AppPackageArchive.ExportZipAsync("helloworld", studio, Ct);
        var verifiedCopy = await AppPackageArchive.ImportZipAsync(zipBytes, studio: null, services: runtime.Services, ct: Ct);
        Assert.True(verifiedCopy.Succeeded, string.Join("\n", verifiedCopy.Errors));
        Assert.Equal(["HelloWorld.cs", "SKILL.md", "scenarios.json", "spec.md"], verifiedCopy.Files.Keys.Order(StringComparer.Ordinal).ToArray());

        // 4. Approve install consent and click "toggle-hello" off -> on -> off on real Silo
        var pending = Assert.Single(await installs.Pending(Ct), p => p.Alias == "helloworld");
        await runtime.ApproveAsPerson(pending.PlanId, Ct);
        for (var i = 0; i < 100 && await installs.Describe("helloworld", Ct) is null; i++) { await Task.Delay(50, Ct); }

        var reader = runtime.Get<IViewReader>(new NeuronId(runtime.Brain, "helloworld", "default"));
        var v1 = await WaitForViewGenerationAsync(reader, "helloworld", minGeneration: 1);
        Assert.DoesNotContain("Hello, IntoChat OS!", JsonSerializer.Serialize(v1, ViewJson), StringComparison.Ordinal);

        await reader.Activate("helloworld", v1.Generation, "toggle-hello", null, "person");
        var v2 = await WaitForViewGenerationAsync(reader, "helloworld", minGeneration: v1.Generation + 1);
        Assert.Contains("Hello, IntoChat OS!", JsonSerializer.Serialize(v2, ViewJson), StringComparison.Ordinal);

        await reader.Activate("helloworld", v2.Generation, "toggle-hello", null, "person");
        var v3 = await WaitForViewGenerationAsync(reader, "helloworld", minGeneration: v2.Generation + 1);
        Assert.DoesNotContain("Hello, IntoChat OS!", JsonSerializer.Serialize(v3, ViewJson), StringComparison.Ordinal);
    }

    private static async Task<PublishedView> WaitForViewGenerationAsync(IViewReader reader, string alias, long minGeneration)
    {
        PublishedView? last = null;
        for (var i = 0; i < 200; i++)
        {
            last = await reader.Read(alias);
            if (last is not null && last.Generation >= minGeneration)
            {
                return last;
            }
            await Task.Delay(50, Ct);
        }

        Assert.Fail($"View '{alias}' did not reach generation {minGeneration}; last was {last?.Generation}.");
        return last!;
    }
}
