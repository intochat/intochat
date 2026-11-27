using System.Runtime.CompilerServices;
using System.Security;
using IntoChat.OS.Modules.Devices;
using DigitalBrain.Capabilities.Connections.Integrations;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Ollama;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class LocalLlmCoderFacts : IDisposable
{
    private readonly string _treePath;

    public LocalLlmCoderFacts()
    {
        _treePath = Path.Combine(Path.GetTempPath(), "intochat-coder-facts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_treePath);
        File.WriteAllText(Path.Combine(_treePath, ".git"), "gitdir: C:/fake/gitdir/worktrees/wt1\n");
        Directory.CreateDirectory(Path.Combine(_treePath, "code"));
        File.WriteAllText(Path.Combine(_treePath, "code", "App.cs"), "namespace Demo;\npublic static class App { public static int V => 1; }\n");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_treePath))
            {
                Directory.Delete(_treePath, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void SandboxBlocksTraversalDotGitAndInstructionFiles()
    {
        var sandbox = new LocalLlmCoder.ConfinedWorkTreeSandbox(_treePath);

        Assert.Throws<SecurityException>(() => sandbox.ResolveConfinedPath("../outside.cs", requireWriteAllowed: false, allowDirectory: false));
        Assert.Throws<SecurityException>(() => sandbox.ResolveConfinedPath(".git", requireWriteAllowed: false, allowDirectory: false));
        Assert.Throws<SecurityException>(() => sandbox.ResolveConfinedPath(".github/workflows/ci.yml", requireWriteAllowed: true, allowDirectory: false));
        Assert.Throws<SecurityException>(() => sandbox.ResolveConfinedPath("CLAUDE.md", requireWriteAllowed: true, allowDirectory: false));
        Assert.Throws<SecurityException>(() => sandbox.ResolveConfinedPath("code/AGENTS.md", requireWriteAllowed: true, allowDirectory: false));
        Assert.Throws<SecurityException>(() => sandbox.ResolveConfinedPath("code/payload.exe", requireWriteAllowed: true, allowDirectory: false));

        var listText = sandbox.ListFiles(null, null);
        Assert.Contains("code/App.cs", listText, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", listText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalLlmCoderExecutesToolLoopViaIModelInferenceAndEditsConfinedFiles()
    {
        var inference = new ScriptedModelInference(
            _ => new AiMessage("assistant",
            [
                new AiToolCall("call-0", "list_files", "{}"),
                new AiToolCall("call-1", "read_file", """{"path":"code/App.cs"}"""),
            ]),
            req =>
            {
                var toolResults = req.Messages[^1].Content.OfType<AiToolResult>().ToArray();
                Assert.Equal(2, toolResults.Length);
                foreach (var tr in toolResults)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(tr.ResultJson);
                    Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
                }

                _ = InferenceMapping.ToChatMessage(req.Messages[^1]);
                Assert.Contains("public static int V => 1;", toolResults[1].ResultJson, StringComparison.Ordinal);
                return new AiMessage("assistant",
                [
                    new AiToolCall("call-2", "replace_in_file", """{"path":"code/App.cs","old_text":"public static int V => 1;","new_text":"public static int V => 42;"}"""),
                ]);
            },
            _ => new AiMessage("assistant",
            [
                new AiText("Changed constant V from 1 to 42."),
            ]));

        var services = new ServiceCollection()
            .AddSingleton<IModelInference>(inference)
            .BuildServiceProvider();

        var coder = new LocalLlmCoder(services);
        var lease = new WorkTreeLease(
            RunId: "run-1",
            RepositoryId: "os",
            SourceRepoPath: _treePath,
            TreePath: _treePath,
            RealGitDirPath: _treePath,
            GitPointerSha256Hex: new string('b', 64),
            BaseBranch: "main",
            BaseCommitSha: new string('a', 40),
            BranchName: "ino/update-v-run1",
            GitHubRepo: "intochat/os");

        var edit = await coder.EditAsync(lease, "Change App.V from 1 to 42", Ct);

        Assert.Contains("42", edit.Summary, StringComparison.Ordinal);
        Assert.Equal(["code/App.cs"], edit.TouchedFiles);
        var updated = await File.ReadAllTextAsync(Path.Combine(_treePath, "code", "App.cs"), Ct);
        Assert.Contains("public static int V => 42;", updated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalLlmCoderOmitsTemperatureWhenModelDescriptorReportsUnknownTemperatureSupport()
    {
        InferenceOptions? capturedOptions = new(Temperature: 0.9f);
        var inference = new ScriptedModelInference(
            req =>
            {
                capturedOptions = req.Options;
                return new AiMessage(
                    "assistant",
                    [
                        new AiToolCall("call-write", "write_file", """{"path":"code/Note.txt","content":"updated\n"}"""),
                    ]);
            },
            req =>
            {
                capturedOptions = req.Options;
                return new AiMessage(
                    "assistant",
                    [
                        new AiText("Added Note.txt."),
                    ]);
            })
        {
            TemperatureSupport = CapabilitySupport.Unknown,
        };

        var services = new ServiceCollection()
            .AddSingleton<IModelInference>(inference)
            .BuildServiceProvider();
        var coder = new LocalLlmCoder(services);
        var lease = new WorkTreeLease(
            RunId: "run-2",
            RepositoryId: "os",
            SourceRepoPath: _treePath,
            TreePath: _treePath,
            RealGitDirPath: _treePath,
            GitPointerSha256Hex: new string('b', 64),
            BaseBranch: "main",
            BaseCommitSha: new string('a', 40),
            BranchName: "ino/update-note-run2",
            GitHubRepo: "intochat/os");

        var edit = await coder.EditAsync(lease, "Add Note.txt", Ct);
        Assert.Null(capturedOptions);
        Assert.Equal(["code/Note.txt"], edit.TouchedFiles);
    }

    [Fact]
    public async Task LocalLlmCoderThrowsTimeoutExceptionOnInferenceTimeoutInsteadOfOperationCanceled()
    {
        var inference = new SlowTimeoutInference();
        var services = new ServiceCollection()
            .AddSingleton<IModelInference>(inference)
            .BuildServiceProvider();

        var coder = new LocalLlmCoder(
            services,
            options: new LocalLlmCoderOptions { Timeout = TimeSpan.FromMilliseconds(50) });
        var lease = new WorkTreeLease(
            RunId: "run-timeout",
            RepositoryId: "os",
            SourceRepoPath: _treePath,
            TreePath: _treePath,
            RealGitDirPath: _treePath,
            GitPointerSha256Hex: new string('b', 64),
            BaseBranch: "main",
            BaseCommitSha: new string('a', 40),
            BranchName: "ino/timeout-run",
            GitHubRepo: "intochat/os");

        await Assert.ThrowsAsync<TimeoutException>(() => coder.EditAsync(lease, "Do work", Ct));
    }

    [Fact]
    public async Task LocalLlmCoderRendersChangePolicyIntoPromptAndEnforcesPolicyLimits()
    {
        var customPolicy = new ChangePolicy(
            maxChangedFiles: 1,
            allowedExtensions: [".cs"],
            blockedFileNames: ["SECRET_NOTES.cs"]);

        string? systemPrompt = null;
        var inference = new ScriptedModelInference(
            req =>
            {
                systemPrompt = string.Join("\n", req.Messages[0].Content.OfType<AiText>().Select(t => t.Text));
                return new AiMessage(
                    "assistant",
                    [
                        new AiToolCall("call-bad-ext", "write_file", """{"path":"README.md","content":"disallowed"}"""),
                        new AiToolCall("call-ok", "write_file", """{"path":"code/First.cs","content":"namespace Demo;"}"""),
                        new AiToolCall("call-over-budget", "write_file", """{"path":"code/Second.cs","content":"namespace Demo;"}"""),
                    ]);
            },
            req =>
            {
                var results = req.Messages[^1].Content.OfType<AiToolResult>().ToArray();
                Assert.Contains("Path gate rejected disallowed file extension", results[0].ResultJson, StringComparison.Ordinal);
                Assert.Contains("\"ok\":true", results[1].ResultJson, StringComparison.Ordinal);
                Assert.Contains("Maximum of 1 modified files reached", results[2].ResultJson, StringComparison.Ordinal);
                return new AiMessage("assistant", [new AiText("Added First.cs within policy.")]);
            });

        var services = new ServiceCollection()
            .AddSingleton<IModelInference>(inference)
            .BuildServiceProvider();

        var coder = new LocalLlmCoder(services, policy: customPolicy);
        var lease = new WorkTreeLease(
            RunId: "run-policy",
            RepositoryId: "os",
            SourceRepoPath: _treePath,
            TreePath: _treePath,
            RealGitDirPath: _treePath,
            GitPointerSha256Hex: new string('b', 64),
            BaseBranch: "main",
            BaseCommitSha: new string('a', 40),
            BranchName: "ino/policy-run",
            GitHubRepo: "intochat/os");

        var edit = await coder.EditAsync(lease, "Add First.cs", Ct);

        Assert.NotNull(systemPrompt);
        Assert.Contains(customPolicy.Describe(), systemPrompt!, StringComparison.Ordinal);
        Assert.Equal(["code/First.cs"], edit.TouchedFiles);
    }

    [Fact]
    public async Task NoThinkModel_ThrowsNoProviderConfiguredInsteadOfDowngradingToChatModel()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IModuleIntegrations<AIModule>>(new UnconfiguredAiIntegrations());
        services.AddSingleton<ILocalRuntime>(new FakeOllamaRuntime(null, KnownModels.Qwen35));
        AIModule.ConfigureServices(services, config);
        var inferenceCalls = 0;
        services.AddSingleton<IModelInference>(new ScriptedModelInference(
            _ =>
            {
                inferenceCalls++;
                return new AiMessage("assistant", [new AiToolCall("call-1", "write_file", """{"path":"code/Downgraded.cs","content":"// bad"}""")]);
            },
            _ => new AiMessage("assistant", [new AiText("done")])));

        await using var sp = services.BuildServiceProvider();
        var coder = new LocalLlmCoder(sp, config);
        var lease = CreateLease("run-no-think");

        var ex = await Assert.ThrowsAsync<NoProviderConfiguredException>(() => coder.EditAsync(lease, "Add feature", Ct));
        Assert.Contains("Think", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(KnownModels.Defaults.Device[ModelRole.Think].Value, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, inferenceCalls);
    }

    [Fact]
    public async Task OllamaRequest_IncludesConfiguredNumCtx()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                ["IntoChatOS:Coder:ContextWindowTokens"] = "16384",
            })
            .Build();

        ChatOptions? capturedChatOptions = null;
        var chatClient = new CapturingScriptedChatClient(
            (_, options) =>
            {
                capturedChatOptions = options;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant,
                [
                    new FunctionCallContent("call-w", "write_file", new Dictionary<string, object?>
                    {
                        ["path"] = "code/NumCtx.cs",
                        ["content"] = "namespace Demo;\npublic static class NumCtx {}\n",
                    }),
                ]));
            },
            (_, options) =>
            {
                capturedChatOptions = options;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Added NumCtx.cs."));
            });

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IModuleIntegrations<AIModule>>(new UnconfiguredAiIntegrations());
        services.AddSingleton<ILocalRuntime>(new FakeOllamaRuntime(chatClient, KnownModels.Qwen25Coder14B));
        AIModule.ConfigureServices(services, config);

        InferenceRequest? capturedInferenceRequest = null;
        services.AddSingleton<IModelInference>(sp =>
            new SpyModelInference(sp.GetRequiredService<InferenceService>(), req => capturedInferenceRequest = req));

        await using var sp = services.BuildServiceProvider();
        var coder = new LocalLlmCoder(sp, config);
        var lease = CreateLease("run-num-ctx");

        var edit = await coder.EditAsync(lease, "Add NumCtx.cs", Ct);

        Assert.Equal(["code/NumCtx.cs"], edit.TouchedFiles);
        Assert.NotNull(capturedInferenceRequest?.Options?.Provider);
        var ollamaOpts = Assert.IsType<OllamaInferenceOptions>(capturedInferenceRequest!.Options!.Provider);
        Assert.Equal(16384, ollamaOpts.ContextWindowTokens);
        Assert.NotNull(capturedChatOptions?.AdditionalProperties);
        Assert.Equal(16384, capturedChatOptions!.AdditionalProperties!["num_ctx"]);
    }

    [Fact]
    public void BindOptions_ReadsMaxToolRoundsTimeoutAndContextWindow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IntoChatOS:Coder:Model"] = "qwen2.5-coder:14b",
                ["IntoChatOS:Coder:MaxToolRounds"] = "24",
                ["IntoChatOS:Coder:TimeoutSeconds"] = "420",
                ["IntoChatOS:Coder:ContextWindowTokens"] = "24576",
            })
            .Build();

        var options = LocalLlmCoder.BindOptions(config);

        Assert.Equal("qwen2.5-coder:14b", options.PreferredModel);
        Assert.Equal(24, options.MaxToolRounds);
        Assert.Equal(TimeSpan.FromSeconds(420), options.Timeout);
        Assert.Equal(24576, options.ContextWindowTokens);
    }

    [Fact]
    public void RoslynBatchEditTool_DeclaresTypedArrayAndItemSchema()
    {
        var sandbox = new LocalLlmCoder.ConfinedWorkTreeSandbox(
            _treePath,
            batchOrchestrator: new InoCodeOrchestrator());

        var tools = sandbox.CreateAIFunctions();
        var batchTool = Assert.IsAssignableFrom<AIFunction>(
            Assert.Single(tools, t => string.Equals(t.Name, "roslyn_batch_edit", StringComparison.Ordinal)));

        var schema = batchTool.JsonSchema;
        var editsProp = schema.GetProperty("properties").GetProperty("edits");
        Assert.Equal("array", editsProp.GetProperty("type").GetString());

        var items = editsProp.GetProperty("items");
        Assert.Equal("object", items.GetProperty("type").GetString());

        var itemProps = items.GetProperty("properties");
        foreach (var expectedField in new[]
        {
            "kind",
            "path",
            "type_name",
            "member_name",
            "namespace_name",
            "source",
            "type_kind",
            "base_types",
        })
        {
            Assert.True(itemProps.TryGetProperty(expectedField, out _), $"Expected roslyn_batch_edit item schema to declare '{expectedField}'. Schema: {schema.GetRawText()}");
        }

        var required = items.GetProperty("required")
            .EnumerateArray()
            .Select(static e => e.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("kind", required);
        Assert.Contains("path", required);
    }

    [Fact]
    public async Task ExecuteToolCallingSessionAsync_NeverExceedsMaxModelCalls()
    {
        var modelCalls = 0;
        var chatClient = new EndlessToolCallChatClient(() =>
        {
            modelCalls++;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent($"call-{modelCalls}", "list_files", new Dictionary<string, object?>()),
            ]));
        });

        var resolved = new ModelCatalog.ResolvedRoleClient(
            chatClient,
            new ResolvedModel(AiProvider.Ollama, "qwen2.5-coder:14b", Endpoint: null, Profile: "default", Revision: "local", Abilities.Tools, Reasoning: null, MaxOutputTokens: null),
            new ModelDescriptor("Ollama", "qwen2.5-coder:14b", "default", "local", CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Supported),
            Selection: null);

        var sandbox = new LocalLlmCoder.ConfinedWorkTreeSandbox(_treePath);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a coder."),
            new(ChatRole.User, "Inspect files."),
        };

        await LocalLlmCoder.ExecuteToolCallingSessionAsync(
            resolved,
            messages,
            sandbox.CreateAIFunctions(),
            maxModelCalls: 2,
            isWorkDone: () => false,
            defaultSummary: "fallback",
            nudgeMessage: "Continue.",
            loggerFactory: null,
            ct: Ct);

        Assert.Equal(2, modelCalls);
    }

    private WorkTreeLease CreateLease(string runId) => new(
        RunId: runId,
        RepositoryId: "os",
        SourceRepoPath: _treePath,
        TreePath: _treePath,
        RealGitDirPath: _treePath,
        GitPointerSha256Hex: new string('b', 64),
        BaseBranch: "main",
        BaseCommitSha: new string('a', 40),
        BranchName: "ino/" + runId,
        GitHubRepo: "intochat/os");

    private sealed class UnconfiguredAiIntegrations : IModuleIntegrations<AIModule>
    {
        public IIntegrationHandle Get(string integrationId) => new UnconfiguredHandle(integrationId);

        private sealed class UnconfiguredHandle(string id) : IIntegrationHandle
        {
            public string IntegrationId => id;
            public Task<IntegrationSnapshot> ReadAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new IntegrationSnapshot { IntegrationId = id, Status = IntegrationStatus.Unconfigured, MissingFields = ["api_key"] });
            public Task<string> ReadSecretAsync(string field, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Unconfigured");
            public Task<ReleasedIntegration> ReleaseAsync(CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("Unconfigured");
        }
    }

    private sealed class FakeOllamaRuntime(IChatClient? chatClient, params ModelCard[] installed) : ILocalRuntime
    {
        public RuntimeId Id => RuntimeId.Ollama;

        public Task<RuntimeStatus> Probe(CancellationToken ct)
            => Task.FromResult(new RuntimeStatus(Id, Reachable: true, Version: "0.9.0", Endpoint: new Uri("http://127.0.0.1:11434")));

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in installed) { yield return card; }
        }

        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var card in installed)
            {
                ct.ThrowIfCancellationRequested();
                yield return card;
            }
        }

        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;

        public IChatClient Chat(ModelId id) => chatClient ?? throw new NotSupportedException("Chat client should not be invoked.");
    }

    private sealed class CapturingScriptedChatClient(params Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse>[] turns) : IChatClient
    {
        private readonly Queue<Func<IEnumerable<ChatMessage>, ChatOptions?, ChatResponse>> _turns = new(turns);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_turns.Dequeue()(messages, options));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class EndlessToolCallChatClient(Func<ChatResponse> onCall) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(onCall());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class SpyModelInference(IModelInference inner, Action<InferenceRequest> onGenerate) : IModelInference
    {
        public bool HasProfile(string name) => inner.HasProfile(name);

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => inner.Describe(selection, model, cancellationToken, brainId);

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            onGenerate(request);
            return inner.Generate(request, model, cancellationToken, brainId);
        }

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => inner.GenerateStreaming(request, model, cancellationToken, brainId);
    }

    private sealed class SlowTimeoutInference : IModelInference
    {
        public bool HasProfile(string name) => true;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId) =>
            Task.FromResult(new ModelDescriptor(
                "Ollama",
                "qwen2.5-coder:14b",
                "default",
                "local",
                CapabilitySupport.Supported,
                CapabilitySupport.Unsupported,
                CapabilitySupport.Supported));

        public async Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new InferenceResult([], "stop", null, null, "qwen2.5-coder:14b");
        }

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId) =>
            throw new NotSupportedException();
    }

    private sealed class ScriptedModelInference(params Func<InferenceRequest, AiMessage>[] turns) : IModelInference
    {
        private readonly Queue<Func<InferenceRequest, AiMessage>> _turns = new(turns);
        public CapabilitySupport TemperatureSupport { get; init; } = CapabilitySupport.Supported;

        public bool HasProfile(string name) => true;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId) =>
            Task.FromResult(new ModelDescriptor(
                "Ollama",
                "qwen2.5-coder:14b",
                "default",
                "local",
                CapabilitySupport.Supported,
                CapabilitySupport.Unsupported,
                TemperatureSupport));

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            var next = _turns.Dequeue()(request);
            return Task.FromResult(new InferenceResult([next], "stop", null, null, "qwen2.5-coder:14b"));
        }

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId) =>
            throw new NotSupportedException();
    }
}
