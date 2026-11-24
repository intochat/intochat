using System.Diagnostics;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// What the engineering-run facts share: a throwaway git repository with a bare origin, scripted stage ports, and a fixture that
// hosts the Catalog module with them. No model, no network and no real remote: the origin is a bare repository on disk.
internal sealed class EngineerRunsKit : IDisposable
{
    private readonly string _root;

    public EngineerRunsKit(string name)
    {
        _root = Path.Combine(Path.GetTempPath(), $"intochat-{name}-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public (string BareOrigin, string Checkout, WorkTreeService WorkTree) CreateRepository(string subName)
    {
        var subRoot = Path.Combine(_root, subName);
        var bareOrigin = Path.Combine(subRoot, "origin.git");
        var checkout = Path.Combine(subRoot, "source-repo");
        var home = Path.Combine(subRoot, "home");
        Directory.CreateDirectory(bareOrigin);
        Directory.CreateDirectory(checkout);
        Directory.CreateDirectory(home);

        Git(bareOrigin, "init", "--bare", "--initial-branch=main");
        Git(checkout, "init", "--initial-branch=main");
        Git(checkout, "config", "user.name", "Repo Owner");
        Git(checkout, "config", "user.email", "owner@example.test");

        Directory.CreateDirectory(Path.Combine(checkout, "code"));
        File.WriteAllText(Path.Combine(checkout, "code", "Calculator.cs"), """
            namespace Demo;

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }
            """);

        Git(checkout, "add", ".");
        Git(checkout, "commit", "-m", "Initial commit");
        Git(checkout, "remote", "add", "origin", bareOrigin);
        Git(checkout, "push", "-u", "origin", "main");

        var workTree = new WorkTreeService(options: new WorkTreeOptions
        {
            HomeDirectory = home,
            VerifyFormatting = false,
        });
        workTree.RegisterRepository(new RegisteredRepository("os", checkout, DefaultBaseBranch: "main"));
        return (bareOrigin, checkout, workTree);
    }

    public static string Git(string workingDirectory, params string[] args)
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

    internal sealed class Fixture(
        IWorkTree workTree,
        IEngineerBuildVerifier buildVerifier,
        ILiveVerifier? liveVerifier,
        ICoder? coder = null) : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlowsFactsModule)];

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(IntoChat.OS.Modules.AI.Agents.Context.Signals.ContextUpdated).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.TryAddSingleton(TimeProvider.System);
            Ports(services);
        }

        protected override void ConfigureTestOS(IServiceCollection services) => Ports(services);

        private void Ports(IServiceCollection services)
        {
            services.AddSingleton(workTree);
            services.AddSingleton<IEngineerPlanner>(new OneTaskPlanner());
            services.AddSingleton<IEngineeringHarness>(new MultiplyHarness());
            services.AddSingleton(buildVerifier);
            // No verifier given: the product's own, registered as the catalog module registers it, with nothing configured.
            if (liveVerifier is not null) { services.AddSingleton(liveVerifier); }
            else { services.AddSingleton<ILiveVerifier, EngineerLiveVerifier>(); }
            services.AddSingleton<IWorktreeRoslyn>(_ => new WorktreeRoslynAnalyzer());
            if (coder is not null) { services.AddSingleton(coder); }
        }
    }

    internal sealed class OneTaskPlanner : IEngineerPlanner
    {
        public Task<EngineerTaskPlan> PlanAsync(
            string repositoryId,
            string goal,
            IReadOnlyList<string> allowedExtensions,
            int maxChangedFiles,
            CancellationToken ct = default)
            => Task.FromResult(new EngineerTaskPlan(
                Goal: goal,
                ArchitectureSummary: "Update Calculator.cs.",
                CriticReview: "Approved.",
                Transcript: [],
                Tasks:
                [
                    new(
                        Id: "t1",
                        Title: "Implement Multiply",
                        Language: "csharp",
                        TargetFiles: ["code/Calculator.cs"],
                        DependsOn: [],
                        Instructions: "Add Multiply(int a, int b) to Calculator."),
                ]));
    }

    // Writes one Multiply method per worktree; each candidate differs by a comment so two worktrees make two commits.
    internal sealed class MultiplyHarness : IEngineeringHarness
    {
        public EngineeringHarnessKind Kind => EngineeringHarnessKind.InoNative;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<EngineeringHarnessResult> ExecuteAsync(
            EngineeringHarnessRequest request,
            Func<OperationProgressed, Task>? onProgress = null,
            CancellationToken ct = default)
        {
            await File.WriteAllTextAsync(
                Path.Combine(request.WorktreePath, "code", "Calculator.cs"),
                $$"""
                namespace Demo;

                public static class Calculator
                {
                    public static int Add(int a, int b) => a + b;
                    // Candidate {{request.InstanceIndex + 1}}
                    public static int Multiply(int a, int b) => a * b;
                }
                """,
                ct).ConfigureAwait(false);

            var slices = (request.RatifiedPlan?.Slices ?? [])
                .Select(static s => s with { Status = "completed", Percent = 100 })
                .ToArray();
            return new EngineeringHarnessResult(
                Succeeded: true,
                Harness: EngineeringHarnessKind.InoNative,
                Summary: "Added Multiply.",
                ModelId: "scripted-model",
                TouchedFiles: ["code/Calculator.cs"],
                DeletedFiles: [],
                CompletedSlices: slices,
                ProgressEvents: []);
        }
    }

    internal sealed class CountingBuildVerifier : IEngineerBuildVerifier
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<EngineerBuildReport> VerifyAsync(string worktreePath, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new EngineerBuildReport(true, true, true, true, 4, 0, 0, 0, [], 10, "Build & tests passed."));
        }
    }

    // Answers with what the fact scripts; green unless told otherwise.
    internal sealed class ScriptedLiveVerifier : ILiveVerifier
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Func<EngineerLiveReport> Answer { get; set; } = Green;

        public Task<EngineerLiveReport> VerifyLiveAsync(string worktreePath, string goal, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Answer());
        }

        public static EngineerLiveReport Green()
            => new(true, true, true, true, 0, 0, 0, 0, [], [], "Live verify green.");
    }

    // A verifier that also says whether a live environment is configured, as the product's own does, and can be switched.
    internal sealed class EnvironmentLiveVerifier : ILiveVerifier, ILiveEnvironment
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Configured { get; set; } = true;

        public Func<EngineerLiveReport> Answer { get; set; } = ScriptedLiveVerifier.Green;

        public Task<EngineerLiveReport> VerifyLiveAsync(string worktreePath, string goal, IReadOnlyList<string> changedFiles, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(Answer());
        }
    }

    // The real worktree service, with a push that can be made to fail as a network error does.
    internal sealed class FaultingWorkTree(IWorkTree inner) : IWorkTree
    {
        public bool FailPush { get; set; }

        public int PushAttempts { get; private set; }

        public IReadOnlyList<RegisteredRepository> ListRepositories() => inner.ListRepositories();
        public void RegisterRepository(RegisteredRepository repository) => inner.RegisterRepository(repository);
        public RegisteredRepository ResolveRepository(RepositoryId repositoryId) => inner.ResolveRepository(repositoryId);
        public Task<WorkTreeLease> PrepareAsync(RepositoryId repositoryId, string runId, string goal, BranchName? baseBranch = null, CancellationToken ct = default)
            => inner.PrepareAsync(repositoryId, runId, goal, baseBranch, ct);
        public Task<IReadOnlyList<WorkTreeLease>> PrepareParallelAsync(RepositoryId repositoryId, string runId, string goal, int instanceCount, BranchName? baseBranch = null, CancellationToken ct = default)
            => inner.PrepareParallelAsync(repositoryId, runId, goal, instanceCount, baseBranch, ct);
        public Task<ChangeSet> VerifyAndCommitAsync(WorkTreeLease lease, string commitMessage, CancellationToken ct = default)
            => inner.VerifyAndCommitAsync(lease, commitMessage, ct);
        public Task<ChangeSet> VerifyAndAmendAsync(WorkTreeLease lease, CommitSha expectedPreviousHead, string commitMessage, CancellationToken ct = default)
            => inner.VerifyAndAmendAsync(lease, expectedPreviousHead, commitMessage, ct);
        public Task<string?> CreateDraftPullRequestAsync(WorkTreeLease lease, string prTitle, string prBody, CancellationToken ct = default)
            => inner.CreateDraftPullRequestAsync(lease, prTitle, prBody, ct);
        public Task<PublishedPrResult> PublishAsync(WorkTreeLease lease, CommitSha approvedSha, string prTitle, string prBody, CancellationToken ct = default)
            => inner.PublishAsync(lease, approvedSha, prTitle, prBody, ct);
        public Task CleanupAsync(WorkTreeLease lease, CancellationToken ct = default) => inner.CleanupAsync(lease, ct);
        public Task CleanupParallelAsync(IReadOnlyList<WorkTreeLease> leases, CancellationToken ct = default) => inner.CleanupParallelAsync(leases, ct);

        public Task<string> PushAsync(WorkTreeLease lease, CommitSha approvedSha, CancellationToken ct = default)
        {
            PushAttempts++;
            return FailPush
                ? throw new IOException("fatal: unable to access the remote: could not resolve host")
                : inner.PushAsync(lease, approvedSha, ct);
        }
    }
}
