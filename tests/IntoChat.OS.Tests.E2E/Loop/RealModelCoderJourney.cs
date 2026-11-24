using System.Diagnostics;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Tests.E2E.Loop;

/// <summary>
/// Live real-model coding journey proving empirically that <see cref="LocalLlmCoder"/>
/// routed through the real <see cref="ModelCatalog"/>, <see cref="IModelInference"/>,
/// <see cref="OllamaRuntime"/> (<c>http://localhost:11434</c>, Think role <c>qwen2.5-coder:14b</c>, no cloud key),
/// and <see cref="WorktreeRoslynAnalyzer"/> can diagnose and fix a failing C# test in a 3-file fixture repository.
/// </summary>
[Trait("needs", "model")]
public sealed class RealModelCoderJourney(ITestOutputHelper output)
{
    private const string OllamaEndpoint = "http://127.0.0.1:11434";
    private const string ExpectedThinkModel = "qwen2.5-coder:14b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LocalLlmCoderWithRealOllamaThinkModelFixesFailingCalculatorTest()
    {
        var fixtureDir = Path.Combine(
            Path.GetTempPath(),
            "intochat-real-model-coder-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(fixtureDir);

        try
        {
            SeedThreeFileFixtureRepository(fixtureDir);

            // 1. Prove the defect is real before calling LocalLlmCoder.
            var before = await RunFixtureTestsAsync(fixtureDir, Ct);
            Assert.NotEqual(0, before.ExitCode);
            Assert.Contains("FAIL: Calculator.Add(2, 3) expected 5 but got -1.", before.StdErr, StringComparison.Ordinal);
            output.WriteLine($"[Before] Fixture exit={before.ExitCode}: {before.StdErr.Trim()}");

            // 2. Wire real OllamaRuntime (http://localhost:11434) + ModelCatalog + InferenceService
            //    + WorktreeRoslynAnalyzer + LocalLlmCoder with NO cloud API keys.
            var devicesSection = ModuleConfiguration.ModuleOptionsSection("devices");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                    [$"{devicesSection}:Ollama:Enabled"] = "true",
                    [$"{devicesSection}:Ollama:Endpoint"] = OllamaEndpoint,
                    [$"{devicesSection}:FoundryLocal:Enabled"] = "false",
                    [$"{devicesSection}:SherpaOnnx:Enabled"] = "false",
                })
                .Build();

            var services = new ServiceCollection();
            services.AddLogging(static b => b.SetMinimumLevel(LogLevel.Information));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IModuleIntegrations<AIModule>>(new UnconfiguredAiIntegrations());
            services.AddSingleton<IWorktreeRoslyn>(new WorktreeRoslynAnalyzer());

            DevicesModule.ConfigureServices(services, configuration);
            AIModule.ConfigureServices(services, configuration);

            // Wrap the real IModelInference (InferenceService) to record empirical rounds, tool calls, and token usage.
            var metrics = new InferenceMetrics();
            var innerInferenceDescriptor = services.Last(static d => d.ServiceType == typeof(IModelInference));
            services.Remove(innerInferenceDescriptor);
            services.AddSingleton<IModelInference>(sp =>
            {
                var inner = innerInferenceDescriptor.ImplementationFactory is not null
                    ? (IModelInference)innerInferenceDescriptor.ImplementationFactory(sp)
                    : (IModelInference)innerInferenceDescriptor.ImplementationInstance!;
                return new MetricsCapturingModelInference(inner, metrics);
            });

            AppsModule.ConfigureServices(services, configuration);
            IntoChat.OS.Modules.Authoring.AuthoringModule.ConfigureServices(services, configuration);

            await using var provider = services.BuildServiceProvider();

            // Verify Ollama runtime is reachable and Think role resolves to qwen2.5-coder:14b.
            var ollamaRuntime = Assert.Single(provider.GetServices<ILocalRuntime>(), static r => r.Id == RuntimeId.Ollama);
            var probe = await ollamaRuntime.Probe(Ct);
            Assert.SkipUnless(probe.Reachable, $"Ollama at {OllamaEndpoint} is not reachable.");

            var catalog = provider.GetRequiredService<ModelCatalog>();
            var resolved = await catalog.Decide(brainId: null, ModelRole.Think, turn: null, requiresTools: true, Ct);
            Assert.Equal(AiProvider.Ollama, resolved.Provider);
            Assert.Equal(ExpectedThinkModel, resolved.Model);

            var coder = provider.GetRequiredService<ICoder>();
            Assert.Equal("LocalLlmCoder", coder.GetType().Name);

            var lease = new WorkTreeLease(
                RunId: "real-model-6-3",
                RepositoryId: "fixture-calc",
                SourceRepoPath: fixtureDir,
                TreePath: fixtureDir,
                RealGitDirPath: fixtureDir,
                GitPointerSha256Hex: new string('0', 64),
                BaseBranch: "main",
                BaseCommitSha: new string('a', 40),
                BranchName: "ino/fix-calculator-add",
                GitHubRepo: "intochat/fixture-calc");

            var goal =
                "Fix the failing test in Calculator.cs so that Calculator.Add(a, b) returns the sum of a and b. " +
                "Do not modify CalculatorTests.cs. " +
                $"Current test failure:\n{before.StdErr.Trim()}";

            // 3. Execute real LocalLlmCoder against local Ollama qwen2.5-coder:14b.
            var sw = Stopwatch.StartNew();
            var edit = await coder.EditAsync(lease, goal, Ct);
            sw.Stop();

            // 4. Log empirical metrics.
            output.WriteLine("=== RealModelCoderJourney Metrics ===");
            output.WriteLine($"Model requested/resolved : {resolved.Model} (edit.ModelId={edit.ModelId})");
            output.WriteLine($"Inference rounds         : {metrics.Rounds}");
            output.WriteLine($"Tool calls ({metrics.ToolCalls.Count})          : {string.Join(", ", metrics.ToolCalls)}");
            output.WriteLine($"Input tokens             : {metrics.InputTokens?.ToString() ?? "n/a"}");
            output.WriteLine($"Output tokens            : {metrics.OutputTokens?.ToString() ?? "n/a"}");
            output.WriteLine($"Total tokens             : {metrics.TotalTokens?.ToString() ?? "n/a"}");
            output.WriteLine($"Wall-clock time          : {sw.Elapsed.TotalSeconds:F2}s ({sw.ElapsedMilliseconds}ms)");
            output.WriteLine($"Touched files            : {string.Join(", ", edit.TouchedFiles)}");
            output.WriteLine($"Summary                  : {edit.Summary}");

            // 5. Assert coder succeeded and modified Calculator.cs.
            Assert.Equal(ExpectedThinkModel, edit.ModelId);
            Assert.Contains(edit.TouchedFiles, static f => string.Equals(f, "Calculator.cs", StringComparison.OrdinalIgnoreCase));
            Assert.True(metrics.Rounds >= 1, "Expected at least one model inference round.");
            Assert.True(metrics.ToolCalls.Count >= 1, "Expected at least one tool call.");

            // 6. Re-run the fixture tests and assert they now PASS.
            var after = await RunFixtureTestsAsync(fixtureDir, Ct);
            output.WriteLine($"[After]  Fixture exit={after.ExitCode}: {after.StdOut.Trim()} {after.StdErr.Trim()}");
            Assert.Equal(0, after.ExitCode);
            Assert.Contains("PASS: all 3 Calculator.Add tests passed.", after.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(fixtureDir);
        }
    }

    private static void SeedThreeFileFixtureRepository(string dir)
    {
        File.WriteAllText(
            Path.Combine(dir, "Fixture.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net11.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
            </Project>
            """);

        File.WriteAllText(
            Path.Combine(dir, "Calculator.cs"),
            """
            namespace Fixture;

            public static class Calculator
            {
                public static int Add(int a, int b) => a - b;
            }
            """);

        File.WriteAllText(
            Path.Combine(dir, "CalculatorTests.cs"),
            """
            namespace Fixture;

            public static class CalculatorTests
            {
                public static int Main()
                {
                    var cases = new (int A, int B, int Expected)[]
                    {
                        (2, 3, 5),
                        (0, 7, 7),
                        (-4, 10, 6),
                    };

                    var failures = 0;
                    foreach (var (a, b, expected) in cases)
                    {
                        var actual = Calculator.Add(a, b);
                        if (actual != expected)
                        {
                            Console.Error.WriteLine($"FAIL: Calculator.Add({a}, {b}) expected {expected} but got {actual}.");
                            failures++;
                        }
                    }

                    if (failures > 0)
                    {
                        Console.Error.WriteLine($"{failures} test(s) failed.");
                        return 1;
                    }

                    Console.WriteLine($"PASS: all {cases.Length} Calculator.Add tests passed.");
                    return 0;
                }
            }
            """);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunFixtureTestsAsync(string fixtureDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixtureDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(Path.Combine(fixtureDir, "Fixture.csproj"));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet process for fixture.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class InferenceMetrics
    {
        public int Rounds { get; private set; }
        public List<string> ToolCalls { get; } = [];
        public long? InputTokens { get; private set; }
        public long? OutputTokens { get; private set; }
        public long? TotalTokens { get; private set; }

        public void Record(InferenceResult result)
        {
            Rounds++;
            foreach (var msg in result.Messages)
            {
                foreach (var call in msg.Content.OfType<AiToolCall>())
                {
                    ToolCalls.Add(call.Name);
                }
            }

            if (result.Usage is { } usage)
            {
                if (usage.InputTokens is { } inp) { InputTokens = (InputTokens ?? 0) + inp; }
                if (usage.OutputTokens is { } outp) { OutputTokens = (OutputTokens ?? 0) + outp; }
                if (usage.TotalTokens is { } tot) { TotalTokens = (TotalTokens ?? 0) + tot; }
            }
        }
    }

    private sealed class MetricsCapturingModelInference(IModelInference inner, InferenceMetrics metrics) : IModelInference
    {
        public bool HasProfile(string name) => inner.HasProfile(name);

        public Task<ModelDescriptor> Describe(ModelSelection? selection = null, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
            => inner.Describe(selection, model, cancellationToken, brainId);

        public async Task<InferenceResult> Generate(InferenceRequest request, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
        {
            var result = await inner.Generate(request, model, cancellationToken, brainId);
            metrics.Record(result);
            return result;
        }

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
            => inner.GenerateStreaming(request, model, cancellationToken, brainId);
    }

    private sealed class UnconfiguredAiIntegrations : IModuleIntegrations<AIModule>
    {
        public IIntegrationHandle Get(string integrationId) => new UnconfiguredHandle(integrationId);

        private sealed class UnconfiguredHandle(string id) : IIntegrationHandle
        {
            public string IntegrationId => id;
            public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new IntegrationSnapshot
                {
                    IntegrationId = id,
                    Status = IntegrationStatus.Unconfigured,
                    MissingFields = ["api_key"],
                });
            public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Unconfigured");
            public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Unconfigured");
        }
    }
}
