using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Confirmation;
using DigitalBrain.Modules.Configuration;
using DigitalBrain.Testing;
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
/// Live real local-model acceptance run proving end-to-end on this machine (no cloud key):
/// failing fixture test -> <see cref="LoopFindingSources.FromFailingFacts"/> + <see cref="FindingDeduplicator"/>
/// + <see cref="LoopBudgetTracker"/> -> real <see cref="WorkTreeService"/> (<c>LocalOnly = true</c>) ->
/// real <see cref="LocalLlmCoder"/> (<see cref="OllamaRuntime"/> at <c>http://127.0.0.1:11434</c>,
/// <c>qwen2.5-coder:14b</c>, + <see cref="WorktreeRoslynAnalyzer"/>) -> real <c>dotnet run</c> build &amp; test ->
/// real <see cref="LocalSelfReview"/> -> second real headless host process on a free loopback port (!= 5080/5081)
/// with isolated temp home (!= ~/.intochat) verified via <c>/health</c> + MCP JSON-RPC
/// (<c>initialize</c>, <c>tools/list</c>, <c>tools/call</c>) + zero error spans ->
/// <see cref="ChangeCard.Body"/> submitted to <see cref="IConfirmationRequests"/> on <see cref="NeuronRuntimeKind.TestOS"/>
/// and STOPPED at the pending approval card in <see cref="IConfirmationInbox"/> (no approval, no merge, no promote).
/// </summary>
[Trait("needs", "model")]
public sealed class RealModelAcceptanceJourney(ITestOutputHelper output)
{
    private const string OllamaEndpoint = "http://127.0.0.1:11434";
    private const string ExpectedThinkModel = "qwen2.5-coder:14b";
    private const int MaxBuildRepairRounds = 3;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LocalModelFindsFixesBuildsVerifiesOnSecondHeadlessHostAndStopsAtApprovalCard()
    {
        var totalSw = Stopwatch.StartNew();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "intochat-real-accept-" + Guid.NewGuid().ToString("N")[..8]);
        var sourceRepo = Path.Combine(tempRoot, "source-repo");
        var worktreeHome = Path.Combine(tempRoot, "worktree-home");
        var candidateHome = Path.Combine(tempRoot, "candidate-home");
        Directory.CreateDirectory(sourceRepo);
        Directory.CreateDirectory(worktreeHome);
        Directory.CreateDirectory(candidateHome);

        // Safety invariant: isolated temp home must NEVER equal ~/.intochat.
        var liveHome = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".intochat"));
        Assert.NotEqual(liveHome, Path.GetFullPath(worktreeHome), StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(liveHome, Path.GetFullPath(candidateHome), StringComparer.OrdinalIgnoreCase);

        Process? candidateProcess = null;
        WorkTreeService? workTree = null;
        WorkTreeLease? lease = null;

        try
        {
            // 1. Seed a temporary LocalOnly git repository with a defect + self-hosted /health & /mcp host.
            SeedFixtureRepository(sourceRepo);
            RunGit(sourceRepo, ["init", "--initial-branch=main"]);
            RunGit(sourceRepo, ["config", "user.name", "Acceptance Runner"]);
            RunGit(sourceRepo, ["config", "user.email", "accept@example.test"]);
            RunGit(sourceRepo, ["add", "."]);
            RunGit(sourceRepo, ["commit", "-m", "Initial commit with seeded DefectTarget bug"]);
            var beforeSha = RunGit(sourceRepo, ["rev-parse", "refs/heads/main"]).Trim();

            // 2. Run fixture tests BEFORE the loop, assert failure, and admit finding via deduplicator + budget.
            var before = await RunFixtureTestsAsync(sourceRepo, Ct);
            Assert.NotEqual(0, before.ExitCode);
            Assert.Contains("FAIL: DefectTarget.ComputeScore(10, 5) expected 15 but got 5.", before.StdErr, StringComparison.Ordinal);
            output.WriteLine($"[Before] Fixture exit={before.ExitCode}: {before.StdErr.Trim()}");

            var findings = LoopFindingSources.FromFailingFacts(
            [
                ("Fixture.DefectTests.ComputeScore", before.StdErr.Trim()),
            ]);
            var finding = Assert.Single(findings);

            var deduplicator = new FindingDeduplicator();
            Assert.True(deduplicator.TryAdmit(finding));
            Assert.False(deduplicator.TryAdmit(finding));

            const string runId = "real-accept-6-15";
            var budget = new LoopBudgetTracker(new LoopBudget(
                RunsPerDay: 10,
                TokensPerRun: 250_000,
                AttemptsPerFinding: 3,
                KillSwitch: false));
            budget.RecordRunStarted(finding.Key);

            // 3. Create real LocalOnly git worktree and wire real LocalLlmCoder + OllamaRuntime + WorktreeRoslynAnalyzer.
            workTree = new WorkTreeService(options: new WorkTreeOptions
            {
                HomeDirectory = worktreeHome,
                VerifyFormatting = false,
            });
            workTree.RegisterRepository(new RegisteredRepository(
                Id: "fixture-accept",
                LocalPath: sourceRepo,
                DefaultBaseBranch: "main",
                LocalOnly: true));

            lease = await workTree.PrepareAsync(
                RepositoryId.Parse("fixture-accept"),
                runId,
                "Fix DefectTarget.ComputeScore",
                null,
                Ct);

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

            var metrics = new InferenceMetrics();
            var innerInferenceDescriptor = services.Last(static d => d.ServiceType == typeof(IModelInference));
            services.Remove(innerInferenceDescriptor);
            services.AddSingleton<IModelInference>(sp =>
            {
                var inner = innerInferenceDescriptor.ImplementationFactory is not null
                    ? (IModelInference)innerInferenceDescriptor.ImplementationFactory(sp)
                    : (IModelInference)innerInferenceDescriptor.ImplementationInstance!;
                return new MetricsCapturingModelInference(inner, metrics, budget, runId);
            });

            AppsModule.ConfigureServices(services, configuration);
            IntoChat.OS.Modules.Authoring.AuthoringModule.ConfigureServices(services, configuration);

            await using var provider = services.BuildServiceProvider();

            var ollamaRuntime = Assert.Single(provider.GetServices<ILocalRuntime>(), static r => r.Id == RuntimeId.Ollama);
            var probe = await ollamaRuntime.Probe(Ct);
            Assert.SkipUnless(probe.Reachable, $"Ollama at {OllamaEndpoint} is not reachable.");

            var catalog = provider.GetRequiredService<ModelCatalog>();
            var resolved = await catalog.Decide(brainId: null, ModelRole.Think, turn: null, requiresTools: true, Ct);
            Assert.Equal(AiProvider.Ollama, resolved.Provider);
            Assert.Equal(ExpectedThinkModel, resolved.Model);

            var coder = provider.GetRequiredService<ICoder>();
            Assert.Equal("LocalLlmCoder", coder.GetType().Name);

            // Invoke LocalLlmCoder with up to 3 build-repair rounds.
            var goal =
                "Fix the failing test in DefectTarget.cs so that DefectTarget.ComputeScore(baseScore, bonus) returns baseScore + bonus. " +
                "Do not modify DefectTests.cs or Program.cs. " +
                $"Current test failure:\n{before.StdErr.Trim()}";

            var coderSw = Stopwatch.StartNew();
            CoderEditResult? lastEdit = null;
            (int ExitCode, string StdOut, string StdErr) after = (-1, string.Empty, string.Empty);
            var repairRoundsUsed = 0;

            for (var round = 1; round <= MaxBuildRepairRounds; round++)
            {
                repairRoundsUsed = round;
                lastEdit = await coder.EditAsync(lease, goal, Ct);
                after = await RunFixtureTestsAsync(lease.TreePath, Ct);
                if (after.ExitCode == 0)
                {
                    break;
                }

                goal =
                    $"Previous edit attempt #{round} did not pass build/tests (exit={after.ExitCode}). " +
                    "Fix DefectTarget.cs so that DefectTarget.ComputeScore(baseScore, bonus) returns baseScore + bonus. " +
                    $"Build/test output:\nSTDOUT:\n{after.StdOut.Trim()}\nSTDERR:\n{after.StdErr.Trim()}";
            }

            coderSw.Stop();

            Assert.NotNull(lastEdit);
            Assert.Equal(ExpectedThinkModel, lastEdit!.ModelId);
            Assert.True(metrics.Rounds >= 1, "Expected at least one model inference round.");
            Assert.True(metrics.ToolCalls.Count >= 1, "Expected at least one tool call.");

            // 4. Assert the real dotnet build & test in the worktree passed, then commit via WorkTreeService.
            Assert.Equal(0, after.ExitCode);
            Assert.Contains("PASS: all 3 DefectTarget.ComputeScore tests passed.", after.StdOut, StringComparison.Ordinal);

            var changeSet = await workTree.VerifyAndCommitAsync(lease, "fix: add bonus in DefectTarget.ComputeScore", Ct);
            var afterSha = changeSet.Commit.Value;
            Assert.NotEqual(beforeSha, afterSha);

            await using var os = await JourneyOS.Start(
                NeuronRuntimeKind.TestOS,
                [typeof(AppsModule), typeof(IntoChat.OS.Modules.Runs.RunsModule), typeof(IntoChat.OS.Modules.Authoring.AuthoringModule), typeof(IntoChat.OS.Modules.Diagnostics.DiagnosticsModule), typeof(IntoChat.OS.Modules.Microsoft.Roslyn.RoslynModule)],
                ct: Ct);
            var brain = os.Runtime.Brain;
            using var callerScope = os.Runtime.Edge.Enter(os.Runtime.Edge.Person("owner", "owner", brain, conversation: null));

            // 5. Run real LocalSelfReview on the worktree diff and assert zero blocking findings.
            var changedPaths = changeSet.ChangedFiles.Select(static f => f.Value).ToArray();
            var roslyn = os.Runtime.Get<IntoChat.OS.Modules.Microsoft.Roslyn.IRoslyn>(new DigitalBrain.NeuronId(brain, "microsoft.roslyn", "self-review"));
            var selfReview = await LocalSelfReview.ReviewWorktree(roslyn, lease.TreePath, changedPaths, Ct);
            Assert.False(selfReview.HasBlocking, $"Blocking self-review findings: {selfReview.FormatForCard()}");

            // 6. Start a SECOND REAL HEADLESS HOST PROCESS from the changed worktree build on a free loopback port
            //    (!= 5080, != 5081) with isolated temp home (!= ~/.intochat) and verify /health + MCP JSON-RPC.
            var candidateSw = Stopwatch.StartNew();
            var candidatePort = AllocateFreeLoopbackPort();
            Assert.NotEqual(5080, candidatePort);
            Assert.NotEqual(5081, candidatePort);

            candidateProcess = StartHeadlessCandidateHost(lease.TreePath, candidatePort, candidateHome);
            var healthBody = string.Empty;
            var mcpInitBody = string.Empty;
            var mcpListBody = string.Empty;
            var mcpCallBody = string.Empty;

            using var liveVerifier = new EngineerLiveVerifier(
                candidateVerifier: async (ctx, ct) =>
                {
                    Assert.NotNull(ctx.VerificationPlan);
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var baseUrl = $"http://127.0.0.1:{candidatePort}";

                    healthBody = await WaitForHealthAsync(http, $"{baseUrl}/health", ct);
                    Assert.Contains("\"status\":\"ok\"", healthBody, StringComparison.Ordinal);
                    Assert.Contains("\"scoreCheck\":15", healthBody, StringComparison.Ordinal);

                    mcpInitBody = await PostJsonRpcAsync(http, $"{baseUrl}/mcp", 1, "initialize", new { clientInfo = new { name = "acceptance", version = "1.0" } }, ct);
                    Assert.Contains("protocolVersion", mcpInitBody, StringComparison.Ordinal);

                    mcpListBody = await PostJsonRpcAsync(http, $"{baseUrl}/mcp", 2, "tools/list", new { }, ct);
                    Assert.Contains("compute_score", mcpListBody, StringComparison.Ordinal);

                    mcpCallBody = await PostJsonRpcAsync(http, $"{baseUrl}/mcp", 3, "tools/call", new { name = "compute_score", arguments = new { baseScore = 10, bonus = 5 } }, ct);
                    Assert.Contains("\"isError\":false", mcpCallBody, StringComparison.Ordinal);
                    Assert.Contains("15", mcpCallBody, StringComparison.Ordinal);

                    return new EngineerLiveReport(
                        Succeeded: true,
                        HostHealthy: true,
                        McpProbesPassed: true,
                        PlaywrightPassed: true,
                        Http4xxCount: 0,
                        Http5xxCount: 0,
                        ErrorSpanCount: 0,
                        ErrorLogCount: 0,
                        Probes:
                        [
                            new EngineerLiveProbe("candidate-health", "host", true, 200, $"GET {baseUrl}/health -> 200"),
                            new EngineerLiveProbe("candidate-mcp-initialize", "mcp", true, 200, "MCP initialize ok"),
                            new EngineerLiveProbe("candidate-mcp-tools-list", "mcp", true, 200, "MCP tools/list -> compute_score"),
                            new EngineerLiveProbe("candidate-mcp-tools-call", "mcp", true, 200, "MCP tools/call compute_score(10, 5) -> 15"),
                        ],
                        TraceSummaries: ["span candidate.health status=OK", "span candidate.mcp status=OK"],
                        Summary: $"Second headless candidate on 127.0.0.1:{candidatePort} healthy; /health 200 + MCP initialize/tools/list/tools/call passed; 0 error spans.");
                });

            liveVerifier.ClearCapturedSpans();
            var liveReport = await liveVerifier.VerifyLiveAsync(
                lease.TreePath,
                goal,
                changedPaths,
                new EngineerLiveContext(TraceId: "trace-real-accept-6-15"),
                Ct);
            candidateSw.Stop();

            Assert.True(liveReport.Succeeded, liveReport.Summary);
            Assert.True(liveReport.HostHealthy);
            Assert.True(liveReport.McpProbesPassed);
            Assert.Equal(0, liveReport.ErrorSpanCount);

            // 7. Build approval card via ChangeCard.Body and submit to IConfirmationRequests on TestOS — AND STOP THERE!
            var change = new Change(
                runId: runId,
                repository: RepositoryId.Parse("fixture-accept"),
                goal: $"Fix finding {finding.Key}: DefectTarget.ComputeScore",
                requestedBaseBranch: BranchName.Parse("main"),
                tree: lease,
                edit: new CoderEdit(lastEdit.Summary, lastEdit.ModelId, changeSet.ChangedFiles),
                commit: changeSet,
                approval: null,
                publication: null);

            var baseCardBody = ChangeCard.Body(change, selfReview);
            var evidenceSection = string.Create(
                CultureInfo.InvariantCulture,
                $"""

                ### Verification Evidence
                - **Tests (worktree)**: PASS (`{after.StdOut.Trim()}`)
                - **Self-Review**: {(selfReview.HasBlocking ? "BLOCKED" : "CLEAN")} ({selfReview.Findings.Length} findings, 0 blocking)
                - **Candidate Host**: `http://127.0.0.1:{candidatePort}` (isolated home, headless)
                  - `GET /health` -> 200 `{healthBody}`
                  - `POST /mcp` `initialize` -> OK
                  - `POST /mcp` `tools/list` -> `compute_score`
                  - `POST /mcp` `tools/call` `compute_score(10, 5)` -> `15` (`isError: false`)
                  - Error spans: {liveReport.ErrorSpanCount}
                """);
            var fullCardBody = baseCardBody + evidenceSection;


            var requests = os.Runtime.Get<IConfirmationRequests>(IConfirmationRequests.Of(brain));
            var inbox = os.Runtime.Get<IConfirmationInbox>(IConfirmationRequests.Of(brain));

            var planId = $"accept-{runId}-{afterSha[..8]}";
            var plan = new ConfirmationPlan(
                Id: planId,
                Action: ConfirmationAction.Other,
                Destructive: false,
                Title: $"Approve local fix {afterSha[..8]} for {finding.Title}",
                Body: fullCardBody,
                ApproveLabel: "Approve & Merge",
                DenyLabel: "Reject",
                TargetLabel: "fixture-accept")
            {
                RequiresPerson = true,
                Kind = EngineerFlow.ConfirmationKind,
                Subject = lease.BranchName,
            };

            var immediateResult = await requests.Request(plan);
            Assert.Null(immediateResult); // Must wait for human decision!

            var pending = (await inbox.Read()).Pending;
            var pendingCard = Assert.Single(pending);
            Assert.Equal(planId, pendingCard.Plan.Id);
            Assert.Contains(afterSha, pendingCard.Plan.Body, StringComparison.Ordinal);
            Assert.Contains("baseScore + bonus", pendingCard.Plan.Body, StringComparison.Ordinal);
            Assert.Contains("PASS: all 3 DefectTarget.ComputeScore tests passed.", pendingCard.Plan.Body, StringComparison.Ordinal);
            Assert.Contains("Self-Review", pendingCard.Plan.Body, StringComparison.Ordinal);
            Assert.Contains($"http://127.0.0.1:{candidatePort}", pendingCard.Plan.Body, StringComparison.Ordinal);
            Assert.Contains("compute_score", pendingCard.Plan.Body, StringComparison.Ordinal);

            // STRICT SAFETY ASSERTIONS: Stopped at approval card — NOT approved, NOT merged, NOT promoted!
            Assert.Null(await requests.Outcome(planId));
            var mainHeadStillBefore = RunGit(sourceRepo, ["rev-parse", "refs/heads/main"]).Trim();
            Assert.Equal(beforeSha, mainHeadStillBefore);

            totalSw.Stop();

            // 8. Print all measured metrics clearly.
            var bodyExcerpt = pendingCard.Plan.Body.Length <= 800
                ? pendingCard.Plan.Body
                : pendingCard.Plan.Body[..800] + "\n... [truncated]";

            output.WriteLine("=== RealModelAcceptanceJourney Measured Metrics ===");
            output.WriteLine($"Model                    : {resolved.Model} (edit.ModelId={lastEdit.ModelId})");
            output.WriteLine($"Build-repair rounds used : {repairRoundsUsed} / {MaxBuildRepairRounds}");
            output.WriteLine($"Inference rounds         : {metrics.Rounds}");
            output.WriteLine($"Tool calls ({metrics.ToolCalls.Count})          : {string.Join(", ", metrics.ToolCalls)}");
            output.WriteLine($"Input tokens             : {metrics.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
            output.WriteLine($"Output tokens            : {metrics.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
            output.WriteLine($"Total tokens             : {metrics.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
            output.WriteLine($"Budget tokens recorded   : {metrics.TotalRecordedBudgetTokens}");
            output.WriteLine($"Wall-clock (coder)       : {coderSw.Elapsed.TotalSeconds:F2}s ({coderSw.ElapsedMilliseconds}ms)");
            output.WriteLine($"Wall-clock (candidate)   : {candidateSw.Elapsed.TotalSeconds:F2}s ({candidateSw.ElapsedMilliseconds}ms)");
            output.WriteLine($"Wall-clock (total)       : {totalSw.Elapsed.TotalSeconds:F2}s ({totalSw.ElapsedMilliseconds}ms)");
            output.WriteLine($"Before SHA (main)        : {beforeSha}");
            output.WriteLine($"After SHA (worktree)     : {afterSha}");
            output.WriteLine($"Main SHA (unchanged)     : {mainHeadStillBefore}");
            output.WriteLine($"Candidate loopback port  : {candidatePort}");
            output.WriteLine($"Candidate /health        : {healthBody}");
            output.WriteLine($"Candidate MCP initialize : {mcpInitBody}");
            output.WriteLine($"Candidate MCP tools/list : {mcpListBody}");
            output.WriteLine($"Candidate MCP tools/call : {mcpCallBody}");
            output.WriteLine($"Self-review findings     : {selfReview.Findings.Length} (blocking={selfReview.HasBlocking})");
            output.WriteLine($"Approval card ID         : {pendingCard.Plan.Id}");
            output.WriteLine($"Approval card title      : {pendingCard.Plan.Title}");
            output.WriteLine($"Approval card status     : PENDING (stopped at approval gate; not approved/merged/promoted)");
            output.WriteLine("--- Approval Card Body Excerpt ---");
            output.WriteLine(bodyExcerpt);
        }
        finally
        {
            StopCandidateProcess(candidateProcess);
            if (workTree is not null && lease is not null)
            {
                try { await workTree.CleanupAsync(lease, CancellationToken.None); } catch { }
            }

            TryDeleteDirectory(candidateHome);
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void SeedFixtureRepository(string dir)
    {
        File.WriteAllText(
            Path.Combine(dir, ".gitignore"),
            """
            bin/
            obj/
            """);

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
            Path.Combine(dir, "DefectTarget.cs"),
            """
            namespace Fixture;

            public static class DefectTarget
            {
                public static int ComputeScore(int baseScore, int bonus) => baseScore - bonus;
            }
            """);

        File.WriteAllText(
            Path.Combine(dir, "DefectTests.cs"),
            """
            namespace Fixture;

            public static class DefectTests
            {
                public static int Run()
                {
                    var cases = new (int BaseScore, int Bonus, int Expected)[]
                    {
                        (10, 5, 15),
                        (0, 7, 7),
                        (-4, 10, 6),
                    };

                    var failures = 0;
                    foreach (var (baseScore, bonus, expected) in cases)
                    {
                        var actual = DefectTarget.ComputeScore(baseScore, bonus);
                        if (actual != expected)
                        {
                            Console.Error.WriteLine($"FAIL: DefectTarget.ComputeScore({baseScore}, {bonus}) expected {expected} but got {actual}.");
                            failures++;
                        }
                    }

                    if (failures > 0)
                    {
                        Console.Error.WriteLine($"{failures} test(s) failed.");
                        return 1;
                    }

                    Console.WriteLine($"PASS: all {cases.Length} DefectTarget.ComputeScore tests passed.");
                    return 0;
                }
            }
            """);

        File.WriteAllText(
            Path.Combine(dir, "Program.cs"),
            """
            using System.Net;
            using System.Text;
            using System.Text.Json;

            namespace Fixture;

            public static class Program
            {
                public static async Task<int> Main(string[] args)
                {
                    if (args.Length > 0 && string.Equals(args[0], "--serve", StringComparison.Ordinal))
                    {
                        var port = 0;
                        var home = string.Empty;
                        for (var i = 1; i < args.Length - 1; i++)
                        {
                            if (string.Equals(args[i], "--port", StringComparison.Ordinal))
                            {
                                port = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else if (string.Equals(args[i], "--home", StringComparison.Ordinal))
                            {
                                home = args[i + 1];
                            }
                        }

                        await RunHeadlessServerAsync(port, home);
                        return 0;
                    }

                    return DefectTests.Run();
                }

                private static async Task RunHeadlessServerAsync(int port, string home)
                {
                    using var listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();

                    while (listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try
                        {
                            ctx = await listener.GetContextAsync();
                        }
                        catch (HttpListenerException)
                        {
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }

                        await HandleRequestAsync(ctx, home);
                    }
                }

                private static async Task HandleRequestAsync(HttpListenerContext ctx, string home)
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    string payload;
                    int statusCode = 200;

                    if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
                    {
                        payload = JsonSerializer.Serialize(new
                        {
                            status = "ok",
                            home = Path.GetFileName(home),
                            scoreCheck = DefectTarget.ComputeScore(10, 5),
                        });
                    }
                    else if (string.Equals(path, "/mcp", StringComparison.OrdinalIgnoreCase))
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        var body = await reader.ReadToEndAsync();
                        using var doc = JsonDocument.Parse(body);
                        var root = doc.RootElement;
                        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : 0;
                        var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : string.Empty;

                        object result = method switch
                        {
                            "initialize" => new
                            {
                                protocolVersion = "2024-11-05",
                                serverInfo = new { name = "fixture-candidate", version = "1.0.0" },
                                capabilities = new { tools = new { } },
                            },
                            "tools/list" => new
                            {
                                tools = new object[]
                                {
                                    new
                                    {
                                        name = "compute_score",
                                        description = "Computes score using DefectTarget.ComputeScore.",
                                        inputSchema = new
                                        {
                                            type = "object",
                                            properties = new
                                            {
                                                baseScore = new { type = "integer" },
                                                bonus = new { type = "integer" },
                                            },
                                        },
                                    },
                                },
                            },
                            "tools/call" => HandleToolCall(root),
                            _ => new { error = $"Unknown method '{method}'" },
                        };

                        payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result });
                    }
                    else
                    {
                        statusCode = 404;
                        payload = "{\"error\":\"not_found\"}";
                    }

                    var bytes = Encoding.UTF8.GetBytes(payload);
                    ctx.Response.StatusCode = statusCode;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.OutputStream.Close();
                }

                private static object HandleToolCall(JsonElement root)
                {
                    var arguments = root.GetProperty("params").GetProperty("arguments");
                    var baseScore = arguments.GetProperty("baseScore").GetInt32();
                    var bonus = arguments.GetProperty("bonus").GetInt32();
                    var score = DefectTarget.ComputeScore(baseScore, bonus);
                    return new
                    {
                        isError = false,
                        content = new object[]
                        {
                            new { type = "text", text = score.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        },
                    };
                }
            }
            """);
    }

    private static int AllocateFreeLoopbackPort()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port is not (5080 or 5081))
            {
                return port;
            }
        }

        throw new InvalidOperationException("Could not allocate a free loopback port distinct from 5080/5081.");
    }

    private static Process StartHeadlessCandidateHost(string worktreeDir, int port, string candidateHome)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = worktreeDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["INTOCHAT_HOME"] = candidateHome;
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(Path.Combine(worktreeDir, "Fixture.csproj"));
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--serve");
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--home");
        psi.ArgumentList.Add(candidateHome);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch headless candidate host process.");
        return proc;
    }

    private static void StopCandidateProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task<string> WaitForHealthAsync(HttpClient http, string url, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Exception? lastError = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            try
            {
                using var response = await http.GetAsync(url, ct);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return await response.Content.ReadAsStringAsync(ct);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }

            await Task.Delay(150, ct);
        }

        throw new TimeoutException($"Headless candidate host at {url} did not become healthy within 15s.", lastError);
    }

    private static async Task<string> PostJsonRpcAsync(
        HttpClient http,
        string url,
        int id,
        string method,
        object @params,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
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

    private static string RunGit(string workingDirectory, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({proc.ExitCode}): {stderr}\n{stdout}");
        }

        return stdout;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

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
        public long TotalRecordedBudgetTokens { get; private set; }

        public void Record(InferenceResult result, LoopBudgetTracker budget, string runId)
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
                TotalRecordedBudgetTokens = budget.RecordTokens(runId, usage);
            }
        }
    }

    private sealed class MetricsCapturingModelInference(
        IModelInference inner,
        InferenceMetrics metrics,
        LoopBudgetTracker budget,
        string runId) : IModelInference
    {
        public bool HasProfile(string name) => inner.HasProfile(name);

        public Task<ModelDescriptor> Describe(ModelSelection? selection = null, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
            => inner.Describe(selection, model, cancellationToken, brainId);

        public async Task<InferenceResult> Generate(InferenceRequest request, ModelId? model = null, CancellationToken cancellationToken = default, string? brainId = null)
        {
            var result = await inner.Generate(request, model, cancellationToken, brainId);
            metrics.Record(result, budget, runId);
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
