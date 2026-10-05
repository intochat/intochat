using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Enforcement;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Enforcement;
using DigitalBrain.Framework.Platform.Identity;

using Orleans.Runtime;

namespace DigitalBrain.Apps;

[GrainType("apps.app")]
internal sealed class App(
    [PersistentState("app", DigitalBrainNames.DefaultGrainStorage)] IPersistentState<AppState> store,
    TimeProvider clock, AppRequirements requirements,
    IEnumerable<IDeployableRuntime> deployables, IEnumerable<IAppStorageHook> storageHooks)
    : Neuron<AppState>(store), IApp, IAppInventoryView
{
    public override NeuronAccess Access(string operation)
    {
        var key = this.GetPrimaryKeyString();
        var caller = CallerContextStamper.Require();
        var addressScope = NeuronAccess.ForBrainKey(key).Scope;
        var creating = operation == nameof(Install) && Snapshot.BrainScopeId is null && Snapshot.Revision is null && Snapshot.Receipts.Count == 0;
        if (caller.Kind == DigitalBrain.Enforcement.CallerKind.App && caller.AppId != key
            && caller.AppId != Snapshot.InstallerAppId && !creating)
        { throw new UnauthorizedAccessException("An app cannot address another installed app."); }
        return new(Snapshot.BrainScopeId ?? addressScope ?? (creating ? BrainScope.CurrentId() : null));
    }

    private const int MaxInvocations = 256;
    private const int MaxSettingLength = 4096;
    private const int MaxPayloadLength = 64 * 1024;

    public Task<AppSnapshot> Read() => Task.FromResult(Describe(Snapshot));

    public Task<NeuronRecord[]> Inventory()
    {
        if (Snapshot.Status != AppStatus.Installed || Snapshot.PendingUninstall is not null || Snapshot.Revision is null)
        { return Task.FromResult<NeuronRecord[]>([]); }
        var package = Snapshot.Revision.Package.ToString();
        return Task.FromResult<NeuronRecord[]>([new("apps", "apps.app", this.GetPrimaryKeyString(), package),
            .. Snapshot.StorageFiles.Select(key => new NeuronRecord("apps", "microsoft.csharp.file", key, package + " / " + key))]);
    }

    // A live app exists the moment it is created: installed, running, editable, allowed red.
    public async Task<AppSnapshot> Create(CreateApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Claim();
        if (Replay(request.OperationId, request)) { return Describe(Snapshot); }
        if (Snapshot.Status == AppStatus.Installed)
        { throw new InvalidOperationException("This app already exists; edit it instead of creating it again."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Runtime);
        await Persist(Snapshot with
        {
            Status = AppStatus.Installed,
            Runtime = request.Runtime,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim() ?? "",
            ProgramGeneration = Snapshot.ProgramGeneration + 1,
            Receipts = Receipted(request.OperationId, request),
            Files = [],
            Document = null,
            Verdicts = [],
            BehaviorErrors = [],
            DeployedFiles = [],
            StorageFiles = [],
        });
        return Describe(Snapshot);
    }

    // Writing a behavior hot-swaps exactly that file. A deploy failure is recorded on the
    // behavior — the app keeps serving; red is a state to revisit, not an error to unwind.
    public async Task<AppSnapshot> WriteBehavior(string path, string source, Guid operationId)
    {
        RequireLive();
        RequireBehaviorPath(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (Replay(operationId, new BehaviorWrite(path, source))) { return Describe(Snapshot); }
        var files = new Dictionary<string, string>(Snapshot.Files, StringComparer.Ordinal) { [path] = source };
        await Persist(Snapshot with { Files = files, Receipts = Receipted(operationId, new BehaviorWrite(path, source)) });
        var errors = new Dictionary<string, string>(Snapshot.BehaviorErrors, StringComparer.Ordinal);
        var deployable = deployables.FirstOrDefault(candidate => candidate.Name == Snapshot.Runtime);
        if (deployable is not null)
        {
            try
            {
                var keys = await deployable.Deploy(new(this.GetPrimaryKeyString(), Snapshot.BrainScopeId ?? "",
                    Snapshot.ProgramGeneration, new(StringComparer.Ordinal) { [path] = source },
                    Configuration(Snapshot.Settings, Snapshot.Accounts)), CancellationToken.None);
                errors.Remove(path);
                var deployed = Snapshot.DeployedFiles.Concat(keys).Distinct(StringComparer.Ordinal).ToArray();
                var storage = Snapshot.StorageFiles.Concat(keys).Distinct(StringComparer.Ordinal).ToArray();
                await Persist(Snapshot with { DeployedFiles = deployed, StorageFiles = storage, BehaviorErrors = errors });
                foreach (var hook in storageHooks)
                { await hook.OnDeployed(Snapshot.BrainScopeId ?? "", this.GetPrimaryKeyString(), storage, CancellationToken.None); }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                errors[path] = failure.Message;
                await Persist(Snapshot with { BehaviorErrors = errors });
            }
        }
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> DeleteBehavior(string path, Guid operationId)
    {
        RequireLive();
        RequireBehaviorPath(path);
        if (Replay(operationId, new BehaviorDelete(path))) { return Describe(Snapshot); }
        var files = new Dictionary<string, string>(Snapshot.Files, StringComparer.Ordinal);
        files.Remove(path);
        var errors = new Dictionary<string, string>(Snapshot.BehaviorErrors, StringComparer.Ordinal);
        errors.Remove(path);
        var deployable = deployables.FirstOrDefault(candidate => candidate.Name == Snapshot.Runtime);
        var deployed = Snapshot.DeployedFiles;
        var storage = Snapshot.StorageFiles;
        if (deployable is not null)
        {
            // Keys are deterministic per (app, generation, path): retiring everything and
            // redeploying what remains keeps the survivors' keys and drops exactly the removed one.
            await deployable.Retire(this.GetPrimaryKeyString(), Snapshot.DeployedFiles, CancellationToken.None);
            var programs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (remaining, source) in files)
            {
                if (remaining == PackageContent.SourcePath
                    || (remaining.StartsWith(PackageContent.BehaviorsPrefix, StringComparison.Ordinal) && remaining.EndsWith(".cs", StringComparison.Ordinal)))
                { programs[remaining] = source; }
            }
            var keys = programs.Count == 0
                ? []
                : await deployable.Deploy(new(this.GetPrimaryKeyString(), Snapshot.BrainScopeId ?? "",
                    Snapshot.ProgramGeneration, programs, Configuration(Snapshot.Settings, Snapshot.Accounts)), CancellationToken.None);
            var removed = Snapshot.DeployedFiles.Except(keys, StringComparer.Ordinal).ToArray();
            deployed = keys;
            storage = Snapshot.StorageFiles.Except(removed, StringComparer.Ordinal).ToArray();
        }
        await Persist(Snapshot with
        {
            Files = files, BehaviorErrors = errors, DeployedFiles = deployed, StorageFiles = storage,
            Receipts = Receipted(operationId, new BehaviorDelete(path)),
        });
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> SaveDocument(AppAuthoringDocument document, Guid operationId)
    {
        RequireLive();
        ArgumentNullException.ThrowIfNull(document);
        if (Replay(operationId, document)) { return Describe(Snapshot); }
        // Verdicts bind to stable scenario ids; a scenario that left the document takes its verdict with it.
        var ids = document.Scenarios.Select(scenario => scenario.Id).ToHashSet(StringComparer.Ordinal);
        var verdicts = Snapshot.Verdicts.Where(verdict => verdict.ScenarioId is { } id && ids.Contains(id)).ToArray();
        await Persist(Snapshot with { Document = document, Verdicts = verdicts, Receipts = Receipted(operationId, document) });
        return Describe(Snapshot);
    }

    public async Task RecordVerdicts(AppScenarioVerdict[] verdicts, Guid operationId)
    {
        RequireLive();
        ArgumentNullException.ThrowIfNull(verdicts);
        if (Replay(operationId, verdicts)) { return; }
        var merged = Snapshot.Verdicts.Where(existing => verdicts.All(update => update.ScenarioId != existing.ScenarioId))
            .Concat(verdicts).ToArray();
        await Persist(Snapshot with { Verdicts = merged, Receipts = Receipted(operationId, verdicts) });
    }

    public Task<AppContent> ReadContent() => Task.FromResult(new AppContent(
        new PackageManifest(Snapshot.Title, Snapshot.Description, Snapshot.Operations.ToArray(), Snapshot.Declared.ToArray(), Runtime: Snapshot.Runtime),
        new Dictionary<string, string>(Snapshot.Files),
        Snapshot.Document,
        Snapshot.Verdicts.ToArray(),
        new Dictionary<string, string>(Snapshot.BehaviorErrors)));

    [GenerateSerializer, Alias("apps.behavior-write")]
    internal sealed record BehaviorWrite([property: Id(0)] string Path, [property: Id(1)] string Source);
    [GenerateSerializer, Alias("apps.behavior-delete")]
    internal sealed record BehaviorDelete([property: Id(0)] string Path);

    private async Task Claim()
    {
        if (Snapshot.BrainScopeId is null && CallerContextStamper.TryGet(out var creator))
        {
            await Persist(Snapshot with
            {
                BrainScopeId = BrainScope.CurrentId(),
                InstallerAppId = creator.Kind == DigitalBrain.Enforcement.CallerKind.App ? creator.AppId : null,
            });
        }
    }

    private void RequireLive()
    {
        RequireInstalled();
        if (Snapshot.Revision is not null)
        { throw new InvalidOperationException("This app runs a package revision; fork it to edit a live copy."); }
    }

    private static void RequireBehaviorPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path != PackageContent.SourcePath
            && !(path.StartsWith(PackageContent.BehaviorsPrefix, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal)))
        { throw new ArgumentException("A behavior is app.cs or a behaviors/*.cs file."); }
    }

    public async Task<AppSnapshot> Install(InstallApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Snapshot.BrainScopeId is null && CallerContextStamper.TryGet(out var installer))
        {
            await Persist(Snapshot with
            {
                BrainScopeId = BrainScope.CurrentId(),
                InstallerAppId = installer.Kind == DigitalBrain.Enforcement.CallerKind.App ? installer.AppId : null,
            });
        }
        await Resume();
        if (Replay(request.OperationId, request)) { return Describe(Snapshot); }
        if (Snapshot.Status == AppStatus.Installed)
        { throw new InvalidOperationException($"{Snapshot.Revision!.Package} is already installed here; configure or upgrade it instead."); }
        var revision = await GrainFactory.GetGrain<IPackage>(request.Revision.Package.ToString()).ReadRevision(request.Revision.Revision);
        requirements.Check(revision.Content);
        var settings = Resolve(revision.Content.Manifest.Settings, new Dictionary<string, string>(), request.Settings);
        var accounts = ResolveAccounts(revision.Content.Manifest.Accounts ?? [], request.Accounts ?? new Dictionary<string, string>());
        var runtime = revision.Content.Manifest.RuntimeName;
        var generation = Snapshot.ProgramGeneration + 1;
        // Track before persisting the install: a failed deployment leaves a harmless candidate,
        // while a completed install can never be hidden by a missed index write.
        var key = this.GetPrimaryKeyString();
        var marker = key.IndexOf("/packages/", StringComparison.Ordinal);
        if (marker > 0 && key[(marker + "/packages/".Length)..] == request.Revision.Package.ToString())
        { await GrainFactory.GetGrain<IApps>(key[..marker]).Track(request.Revision.Package); }
        await DeployAndPersist(Snapshot with
        {
            Status = AppStatus.Installed,
            Runtime = runtime,
            Revision = request.Revision,
            Declared = [.. revision.Content.Manifest.Settings],
            Settings = settings,
            Accounts = accounts,
            Operations = [.. revision.Content.Manifest.Operations],
            ProgramGeneration = generation,
            Receipts = Receipted(request.OperationId, request),
            StorageFiles = [],
            DeployedFiles = [],
        });
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Configure(ConfigureApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Resume();
        if (Replay(request.OperationId, request)) { return Describe(Snapshot); }
        RequireInstalled();
        var settings = Resolve(Snapshot.Declared, Snapshot.Settings, request.Settings);
        var revision = await GrainFactory.GetGrain<IPackage>(Snapshot.Revision!.Package.ToString()).ReadRevision(Snapshot.Revision.Revision);
        requirements.Check(revision.Content);
        var selected = new Dictionary<string, string>(Snapshot.Accounts);
        foreach (var (slot, account) in request.Accounts ?? new Dictionary<string, string>()) { selected[slot] = account; }
        var accounts = ResolveAccounts(revision.Content.Manifest.Accounts ?? [], selected);
        await DeployAndPersist(Snapshot with
        {
            Settings = settings,
            Accounts = accounts,
            ProgramGeneration = Snapshot.ProgramGeneration + 1,
            Receipts = Receipted(request.OperationId, request),
        });
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Upgrade(UpgradeApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Resume();
        if (Replay(request.OperationId, request)) { return Describe(Snapshot); }
        RequireInstalled();
        var installed = Snapshot.Revision!.Package;
        if (request.Revision.Package != installed)
        { throw new ArgumentException($"This app runs {installed}. Install {request.Revision.Package} as its own app to try it."); }
        var revision = await GrainFactory.GetGrain<IPackage>(installed.ToString()).ReadRevision(request.Revision.Revision);
        requirements.Check(revision.Content);
        // Keep what the installer chose for settings the new revision still declares.
        var declared = revision.Content.Manifest.Settings;
        var kept = Snapshot.Settings.Where(pair => declared.Any(setting => setting.Name == pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        var settings = Resolve(declared, kept, new Dictionary<string, string>());
        var accounts = ResolveAccounts(revision.Content.Manifest.Accounts ?? [], request.Accounts ?? Snapshot.Accounts);
        var runtime = revision.Content.Manifest.RuntimeName;
        await DeployAndPersist(Snapshot with
        {
            Runtime = runtime,
            Revision = request.Revision,
            ProgramGeneration = Snapshot.ProgramGeneration + 1,
            Declared = [.. declared],
            Settings = settings,
            Accounts = accounts,
            Operations = [.. revision.Content.Manifest.Operations],
            Receipts = Receipted(request.OperationId, request),
        });
        return Describe(Snapshot);
    }

    public async Task<AppSnapshot> Uninstall(UninstallApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Resume();
        if (Replay(request.OperationId, request)) { return Describe(Snapshot); }
        if (Snapshot.Status != AppStatus.Installed)
        {
            // Even a no-op must remain a no-op when delivery repeats after a later install.
            await Persist(Snapshot with { Receipts = Receipted(request.OperationId, request) });
            return Describe(Snapshot);
        }
        await Persist(Snapshot with { PendingUninstall = request });
        await CompleteUninstall();
        return Describe(Snapshot);
    }

    private async Task CompleteUninstall()
    {
        var request = Snapshot.PendingUninstall!;
        await RetireDeployment(Snapshot.Runtime, Snapshot.StorageFiles);
        foreach (var hook in storageHooks)
        { await hook.OnRetired(Snapshot.BrainScopeId ?? "", this.GetPrimaryKeyString(), Snapshot.StorageFiles, CancellationToken.None); }
        var now = clock.GetUtcNow();
        var invocations = Snapshot.Invocations
            .Select(item => item.Status == InvocationStatus.Pending ? item with { Status = InvocationStatus.Failed, Error = "The app was uninstalled.", CompletedAt = now } : item)
            .ToList();
        await Persist(Snapshot with
        {
            Status = AppStatus.Uninstalled,
            Invocations = invocations,
            Receipts = Receipted(request.OperationId, request),
            PendingUninstall = null,
        });
    }

    public async Task<AppInvocation> Invoke(InvokeApp request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Resume();
        if (request.ExpectedRevision is { } expected && expected != Snapshot.Revision)
        { throw new UnauthorizedAccessException("The selected app revision changed. Discover and select it again."); }
        if (Snapshot.Invocations.Find(item => item.Id == request.InvocationId) is { } existing) { return existing; }
        RequireInstalled();
        if (!Snapshot.Operations.Any(operation => operation.Name == request.Operation))
        {
            throw new ArgumentException(
                $"{request.Operation} is not an operation of {Snapshot.Revision!.Package}. It offers: {string.Join(", ", Snapshot.Operations.Select(operation => operation.Name))}.");
        }
        if (request.Input is not { Length: <= MaxPayloadLength }) { throw new ArgumentException("An invocation input is at most 64 KiB."); }
        var invocation = new AppInvocation(request.InvocationId, request.Operation, request.Input, InvocationStatus.Pending, null, null, clock.GetUtcNow(), null);
        var invocations = new List<AppInvocation>(Snapshot.Invocations) { invocation };
        while (invocations.Count > MaxInvocations)
        {
            var oldest = invocations.FindIndex(item => item.Status != InvocationStatus.Pending);
            if (oldest < 0) { throw new InvalidOperationException($"{MaxInvocations} invocations are still waiting for the app's script."); }
            invocations.RemoveAt(oldest);
        }
        await Save(Snapshot with { Invocations = invocations }, new AppInvoked(invocation.Id, invocation.Operation, invocation.Input));
        if (Snapshot.DeployedFiles.Length == 0)
        { await GrainFactory.GetGrain<IAppRuntimeWorker>(0).Answer(this.GetPrimaryKeyString(), Snapshot.Revision!, invocation); }
        return invocation;
    }

    public async Task<AppInvocation> Respond(AppResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var index = Snapshot.Invocations.FindIndex(item => item.Id == response.InvocationId);
        if (index < 0) { throw new KeyNotFoundException($"No invocation {response.InvocationId}."); }
        var invocation = Snapshot.Invocations[index];
        if (invocation.Status != InvocationStatus.Pending) { return invocation; }
        if (response.Output is { Length: > MaxPayloadLength } || response.Error is { Length: > MaxPayloadLength })
        { throw new ArgumentException("An invocation output is at most 64 KiB."); }
        var completed = invocation with
        {
            Status = response.Error is null ? InvocationStatus.Completed : InvocationStatus.Failed,
            Output = response.Output,
            Error = response.Error,
            CompletedAt = clock.GetUtcNow(),
        };
        var invocations = new List<AppInvocation>(Snapshot.Invocations) { [index] = completed };
        await Save(Snapshot with { Invocations = invocations }, new AppInvocationCompleted(completed));
        return completed;
    }

    public Task<AppInvocation> ReadInvocation(Guid invocationId)
        => Snapshot.Invocations.Find(item => item.Id == invocationId) is { } invocation
            ? Task.FromResult(invocation)
            : throw new KeyNotFoundException($"No invocation {invocationId}.");

    public Task<IReadOnlyList<AppInvocation>> Pending()
        => Task.FromResult<IReadOnlyList<AppInvocation>>(Snapshot.Invocations.Where(item => item.Status == InvocationStatus.Pending).ToArray());

    // A runtime with programs materializes them; every other runtime answers through
    // IAppRuntimeWorker and deploys nothing. The app grain never knows how.
    private async Task<string[]> Deploy(string runtime, int generation, PackageContent content, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> accounts)
    {
        var deployable = deployables.FirstOrDefault(candidate => candidate.Name == runtime);
        if (deployable is null) { return []; }
        var programs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, source) in content.Programs()) { programs[path] = source; }
        return await deployable.Deploy(
            new(this.GetPrimaryKeyString(), Snapshot.BrainScopeId ?? "", generation, programs, Configuration(settings, accounts)),
            CancellationToken.None);
    }

    private async Task RetireDeployment(string runtime, string[] storageKeys)
    {
        if (storageKeys.Length == 0) { return; }
        var deployable = deployables.FirstOrDefault(candidate => candidate.Name == runtime);
        if (deployable is not null) { await deployable.Retire(this.GetPrimaryKeyString(), storageKeys, CancellationToken.None); }
    }

    private async Task Resume()
    {
        if (Snapshot.PendingDeployment is not null) { await CompleteDeployment(); }
        if (Snapshot.PendingUninstall is not null) { await CompleteUninstall(); }
    }

    private async Task DeployAndPersist(AppState target)
    {
        await Persist(Snapshot with { PendingDeployment = new(target) });
        await CompleteDeployment();
    }

    private async Task CompleteDeployment()
    {
        var target = Snapshot.PendingDeployment!.Target;
        var revision = await GrainFactory.GetGrain<IPackage>(target.Revision!.Package.ToString()).ReadRevision(target.Revision.Revision);
        await RetireDeployment(Snapshot.Runtime, Snapshot.DeployedFiles);
        if (CallerContextStamper.TryGet(out _))
        {
            await GrainFactory.GetGrain<IAppGrantMigration>(BrainScope.CurrentId()).Migrate(this.GetPrimaryKeyString(), Snapshot.StorageFiles);
        }
        var deployed = await Deploy(target.Runtime, target.ProgramGeneration, revision.Content, target.Settings, target.Accounts);
        // A resumed deployment re-deploys the same deterministic keys; the union stays stable.
        target = target with
        {
            DeployedFiles = deployed,
            StorageFiles = target.StorageFiles.Concat(deployed).Distinct(StringComparer.Ordinal).ToArray(),
        };
        foreach (var hook in storageHooks)
        { await hook.OnDeployed(target.BrainScopeId ?? "", this.GetPrimaryKeyString(), target.StorageFiles, CancellationToken.None); }
        await Persist(target);
    }

    // The script reads brain.Setting("App") to find this neuron, brain.Setting(name) for each setting
    // and brain.Setting("Account__" + slot) for each connected account.
    private Dictionary<string, string> Configuration(IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string> accounts)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["App"] = this.GetPrimaryKeyString(),
            // The brain is ambient identity: durable state an app keys by it survives the app
            // being renamed, reinstalled or forked.
            ["Brain"] = Snapshot.BrainScopeId ?? "",
        };
        foreach (var (name, value) in settings) { values[name] = value; }
        foreach (var (name, id) in accounts) { values["Account__" + name] = id; }
        return values;
    }

    private static Dictionary<string, string> ResolveAccounts(IReadOnlyList<PackageConnection> declared, IReadOnlyDictionary<string, string> selected)
    {
        if (selected.Keys.Any(name => !declared.Any(slot => slot.Name == name)))
        { throw new ArgumentException("The installation contains an undeclared account slot."); }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in declared)
        {
            if (!selected.TryGetValue(slot.Name, out var id) || string.IsNullOrWhiteSpace(id))
            { throw new ArgumentException($"Choose an account for {slot.Name} ({slot.Source})."); }
            result.Add(slot.Name, id);
        }
        return result;
    }

    private static Dictionary<string, string> Resolve(IReadOnlyList<PackageSetting> declared, IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> supplied)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        var resolved = declared.ToDictionary(setting => setting.Name, setting => current.TryGetValue(setting.Name, out var chosen) ? chosen : setting.DefaultValue, StringComparer.Ordinal);
        foreach (var (name, value) in supplied)
        {
            var setting = declared.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{name} is not a setting of this package. It declares: {string.Join(", ", declared.Select(item => item.Name))}.");
            if (value is not { Length: <= MaxSettingLength }) { throw new ArgumentException($"{setting.Name} is at most {MaxSettingLength} characters."); }
            resolved[setting.Name] = value;
        }
        return resolved;
    }

    private void RequireInstalled()
    {
        if (Snapshot.Status != AppStatus.Installed) { throw new InvalidOperationException("The app is not installed."); }
    }

    private bool Replay(Guid operationId, object request)
    {
        var receipt = Snapshot.Receipts.Find(item => item.OperationId == operationId);
        if (receipt is null) { return false; }
        return receipt.RequestHash == CommandHash(request)
            ? true
            : throw new InvalidOperationException($"Operation {operationId} was already used for a different change.");
    }

    private List<OperationReceipt> Receipted(Guid operationId, object request)
    {
        var receipts = new List<OperationReceipt>(Snapshot.Receipts) { new(operationId, CommandHash(request), "") };
        if (receipts.Count > PackageRules.MaxReceipts) { receipts.RemoveAt(0); }
        return receipts;
    }

    // Settings are a set: the same keys sent in another order are the same command.
    private static string CommandHash(object request) => PackageHash.Of(request switch
    {
        InstallApp install => install with
        {
            Settings = new SortedDictionary<string, string>(install.Settings.ToDictionary(), StringComparer.Ordinal),
            Accounts = new SortedDictionary<string, string>((install.Accounts ?? new Dictionary<string, string>()).ToDictionary(), StringComparer.Ordinal)
        },
        ConfigureApp configure => configure with
        {
            Settings = new SortedDictionary<string, string>(configure.Settings.ToDictionary(), StringComparer.Ordinal),
            Accounts = new SortedDictionary<string, string>((configure.Accounts ?? new Dictionary<string, string>()).ToDictionary(), StringComparer.Ordinal)
        },
        UpgradeApp upgrade => upgrade with { Accounts = new SortedDictionary<string, string>((upgrade.Accounts ?? new Dictionary<string, string>()).ToDictionary(), StringComparer.Ordinal) },
        _ => request,
    });

    private Task Persist(AppState next) => Save(next, new AppChanged(Describe(next)));

    private AppSnapshot Describe(AppState state) => new(
        state.Status,
        state.Revision,
        new Dictionary<string, string>(state.Settings),
        state.Operations.ToArray(),
        state.DeployedFiles.ToArray(),
        new Dictionary<string, string>(state.Accounts), state.PendingUninstall is not null);
}
