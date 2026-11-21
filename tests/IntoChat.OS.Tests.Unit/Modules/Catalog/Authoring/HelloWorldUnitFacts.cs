using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Runtime.Synapses;
using DigitalBrain.Testing;
using DigitalBrain.Testing.Synapses;
using DigitalBrain.Units;
using IntoChat.OS.Apps;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Authoring;

public sealed class HelloWorldUnitFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SynapseCompiler HostCompiler = new(ReferenceSet.FromDefaultContext());

    private static string LocateRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "apps", "helloworld", "HelloWorld.cs");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate apps/helloworld/HelloWorld.cs from test base directory.");
    }

    [Fact]
    public async Task HelloWorldReferenceFolderCompilesPassesBoundaryAndTogglesGreetingOffOnOff()
    {
        var repoRoot = LocateRepoRoot();
        var appDir = Path.Combine(repoRoot, "apps", "helloworld");

        var code = await File.ReadAllTextAsync(Path.Combine(appDir, "HelloWorld.cs"), Ct);
        var scenariosJson = await File.ReadAllTextAsync(Path.Combine(appDir, "scenarios.json"), Ct);
        var skillMd = await File.ReadAllTextAsync(Path.Combine(appDir, "SKILL.md"), Ct);
        var specMd = await File.ReadAllTextAsync(Path.Combine(appDir, "spec.md"), Ct);

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HelloWorld.cs"] = code,
            [UnitFiles.Scenarios] = scenariosJson,
            [UnitFiles.Spec] = specMd,
            [SkillDiscovery.FileName] = skillMd,
        };

        // 1. Closed-world Roslyn compilation + PE boundary check
        var source = new SynapseSource("helloworld", UnitFiles.Code(files), null);
        var build = HostCompiler.Compile(source);
        Assert.True(build.Success, string.Join("\n", build.Diagnostics.Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}")));
        Assert.Null(SynapseCompiler.Verify(build));

        // 2. Zero external slots (Needs = []) + full scenario verification
        var scenarios = JsonSerializer.Deserialize<Scenario[]>(scenariosJson, JsonSerializerOptions.Web)!;
        Assert.NotEmpty(scenarios);
        var needs = new UnitNeeds([], Schedule: null, View: true);
        var check = await BuilderTools.Check(HostCompiler, "helloworld", files, needs, scenarios, Ct);
        Assert.True(
            check.Passed,
            string.Join("\n", check.Diagnostics.Select(d => $"{d.Id}: {d.Message}").Concat(check.Failures)));

        // 3. Step-by-step interactive toggle proof:
        //    (a) Initial view hides "Hello, IntoChat OS!" and shows "Click the button below" + "Say Hello"
        var initialRun = await UnitScenario.Run(source, new FakeSlots(), [], Ct);
        Assert.True(initialRun.Passed, string.Join("\n", initialRun.Failures));
        var initialViewJson = Assert.Single(initialRun.Views, v => v.Alias == "helloworld").View.GetRawText();
        Assert.DoesNotContain("Hello, IntoChat OS!", initialViewJson, StringComparison.Ordinal);
        Assert.Contains("Click the button below", initialViewJson, StringComparison.Ordinal);
        Assert.Contains("toggle-hello", initialViewJson, StringComparison.Ordinal);
        Assert.Contains("Say Hello", initialViewJson, StringComparison.Ordinal);

        //    (b) First click ("helloworld.toggle") reveals "Hello, IntoChat OS!" and flips label to "Hide Hello"
        var oneToggleRun = await UnitScenario.Run(
            source,
            new FakeSlots(),
            [new Publish("helloworld.toggle", JsonDocument.Parse("{}").RootElement)],
            Ct);
        Assert.True(oneToggleRun.Passed, string.Join("\n", oneToggleRun.Failures));
        var visibleViewJson = Assert.Single(oneToggleRun.Views, v => v.Alias == "helloworld").View.GetRawText();
        Assert.Contains("Hello, IntoChat OS!", visibleViewJson, StringComparison.Ordinal);
        Assert.Contains("Hide Hello", visibleViewJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Click the button below", visibleViewJson, StringComparison.Ordinal);

        //    (c) Second click ("helloworld.toggle") hides "Hello, IntoChat OS!" again
        var twoToggleRun = await UnitScenario.Run(
            source,
            new FakeSlots(),
            [
                new Publish("helloworld.toggle", JsonDocument.Parse("{}").RootElement),
                new Publish("helloworld.toggle", JsonDocument.Parse("{}").RootElement),
            ],
            Ct);
        Assert.True(twoToggleRun.Passed, string.Join("\n", twoToggleRun.Failures));
        var hiddenAgainViewJson = Assert.Single(twoToggleRun.Views, v => v.Alias == "helloworld").View.GetRawText();
        Assert.DoesNotContain("Hello, IntoChat OS!", hiddenAgainViewJson, StringComparison.Ordinal);
        Assert.Contains("Click the button below", hiddenAgainViewJson, StringComparison.Ordinal);
        Assert.Contains("Say Hello", hiddenAgainViewJson, StringComparison.Ordinal);

        // 4. ShippedUnits.Files("helloworld") also exposes the interactive unit
        Assert.True(ShippedUnits.IsKnown("helloworld"));
        var shipped = ShippedUnits.Files("helloworld");
        Assert.Contains("HelloWorld.cs", shipped.Keys);
        Assert.Contains("scenarios.json", shipped.Keys);
    }

    [Fact]
    public async Task OfflineSynthesizerProducesVerifiedInteractiveHelloWorldUnit()
    {
        const string goal = "Create a Hello World app where clicking a button makes 'Hello, IntoChat OS!' appear (or toggles visibility)";
        var unitJson = await OfflineUnitSynthesizer.CompleteAsync("", goal, Ct);
        var authored = Creator.Parse<AuthoredUnit>(unitJson);

        Assert.Equal("hello.world", authored.Alias);
        Assert.Empty(authored.Needs.Slots);
        Assert.Null(authored.Needs.Schedule);
        Assert.True(authored.Needs.View);

        var files = OfflineUnitSynthesizer.SynthesizeUnitFiles(unitJson);
        var check = await BuilderTools.Check(HostCompiler, authored.Alias, files, authored.Needs, authored.Scenarios, Ct);
        Assert.True(
            check.Passed,
            string.Join("\n", check.Diagnostics.Select(d => $"{d.Id}: {d.Message}").Concat(check.Failures)));
    }

    [Fact]
    public async Task CreatorDefaultsToAutoModelAndUsesConfiguredProviderWithoutOpenAi()
    {
        // Configure ONLY local Ollama profile via ModelCatalog + IModelInference (zero OpenAI keys or configuration!)
        var config = new ConfigurationBuilder().Build();
        var fakeInference = new ScriptedHelloInference();

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<AIOptions>(o =>
        {
            o.Roles.Device[Role.Chat] = "profile:local-coder";
            o.Roles.Device[Role.Think] = "profile:local-coder";
            o.Roles.Cloud[Role.Chat] = "profile:local-coder";
            o.Roles.Cloud[Role.Think] = "profile:local-coder";
            o.Ollama.Endpoint = "http://127.0.0.1:11434";
            o.ModelProfiles["local-coder"] = new AIModelProfileOptions
            {
                Provider = "Ollama",
                Model = "qwen2.5-coder:14b",
                Endpoint = "http://127.0.0.1:11434",
                Capabilities = Abilities.Tools,
            };
        });
        AIModule.ConfigureServices(services, config);
        services.AddSingleton<IModelInference>(fakeInference);
        using var sp = services.BuildServiceProvider();

        var creator = new Creator(
            sp,
            Options.Create(new AppsOptions { AuthorModel = null, BuilderModel = null }),
            NullLogger<Creator>.Instance,
            model: new SiloCreatorModel(sp),
            compiler: HostCompiler);

        var drafter = (IDrafter)creator;
        var unit = await drafter.Author("local", "Create a Hello World toggle app", existing: null, Ct);
        Assert.Equal("hello.world", unit.Alias);
        Assert.True(fakeInference.Calls >= 1);
    }

    private sealed class ScriptedHelloInference : IModelInference
    {
        public int Calls { get; private set; }

        public string? LastRequestedModel { get; private set; }

        public bool HasProfile(string name) => string.Equals(name, "local-coder", StringComparison.OrdinalIgnoreCase);

        public Task<ModelDescriptor> Describe(
            ModelSelection? selection = null,
            ModelId? model = null,
            CancellationToken cancellationToken = default,
            string? brainId = null)
            => Task.FromResult(new ModelDescriptor(
                "Ollama",
                "qwen2.5-coder:14b",
                "local-coder",
                "rev-1",
                CapabilitySupport.Supported,
                CapabilitySupport.Unknown,
                CapabilitySupport.Unknown,
                null,
                null,
                CapabilitySupport.Unknown,
                CapabilitySupport.Unknown,
                null));

        public async Task<InferenceResult> Generate(
            InferenceRequest request,
            ModelId? model = null,
            CancellationToken cancellationToken = default,
            string? brainId = null)
        {
            Calls++;
            LastRequestedModel = request.Model?.Model;
            var json = await OfflineUnitSynthesizer.CompleteAsync("", "Create a Hello World app", cancellationToken);
            return new InferenceResult(
                [new AiMessage("assistant", [new AiText(json)])],
                "stop",
                new InferenceUsage(12, 48, 60),
                "resp-1",
                "claude-sonnet-4-5");
        }

        public async IAsyncEnumerable<InferenceUpdate> GenerateStreaming(
            InferenceRequest request,
            ModelId? model = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
            string? brainId = null)
        {
            var res = await Generate(request, model, cancellationToken, brainId);
            yield return new InferenceUpdate("assistant", res.Messages[0].Content, res.FinishReason, res.Usage, res.ResponseId, "msg-1", res.Model);
        }
    }
}
