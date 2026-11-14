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
                new AiToolCall("call-1", "read_file", """{"path":"code/App.cs"}"""),
            ]),
            req =>
            {
                var toolResult = req.Messages[^1].Content.OfType<AiToolResult>().Single();
                Assert.Contains("public static int V => 1;", toolResult.ResultJson, StringComparison.Ordinal);
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

    private sealed class ScriptedModelInference(params Func<InferenceRequest, AiMessage>[] turns) : IModelInference
    {
        private readonly Queue<Func<InferenceRequest, AiMessage>> _turns = new(turns);
        public CapabilitySupport TemperatureSupport { get; init; } = CapabilitySupport.Supported;

        public bool HasProfile(string name) => true;

        public Task<ModelDescriptor> Describe(ModelSelection? selection, Type? marker, CancellationToken cancellationToken, string? brainId) =>
            Task.FromResult(new ModelDescriptor(
                "Ollama",
                "qwen2.5-coder:14b",
                "default",
                "local",
                CapabilitySupport.Supported,
                CapabilitySupport.Unsupported,
                TemperatureSupport));

        public Task<InferenceResult> Generate(InferenceRequest request, Type? marker, CancellationToken cancellationToken, string? brainId)
        {
            var next = _turns.Dequeue()(request);
            return Task.FromResult(new InferenceResult([next], "stop", null, null, "qwen2.5-coder:14b"));
        }

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, Type? marker, CancellationToken cancellationToken, string? brainId) =>
            throw new NotSupportedException();
    }
}
