using System.Security;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
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
    public async Task ExternalCliCoderKillsProcessAndThrowsTimeoutExceptionOnTimeout()
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        string[] args = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"]
            : ["-c", "sleep 30"];

        var coder = new ExternalCliCoder(shell, args, TimeSpan.FromMilliseconds(150));
        var lease = new WorkTreeLease(
            RunId: "cli-timeout",
            RepositoryId: "os",
            SourceRepoPath: _treePath,
            TreePath: _treePath,
            RealGitDirPath: _treePath,
            GitPointerSha256Hex: new string('b', 64),
            BaseBranch: "main",
            BaseCommitSha: new string('a', 40),
            BranchName: "ino/cli-timeout",
            GitHubRepo: "intochat/os");

        await Assert.ThrowsAsync<TimeoutException>(() => coder.EditAsync(lease, "Sleep", Ct));
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
