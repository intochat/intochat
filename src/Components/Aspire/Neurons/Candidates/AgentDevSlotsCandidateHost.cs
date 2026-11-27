using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DigitalBrain.Capabilities.Connections.Integrations;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static IntoChat.Aspire.Candidates.CandidateWorkspace;

namespace IntoChat.Aspire.Candidates;

// Plane 2 Dev Mode containment provider (Slice RFC-3: agent-dev-slots + .NET Aspire).
// Runs candidates inside unprivileged (UID 1000) leased devcontainer slots with scrubbed
// ephemeral $HOME mounts, dynamic lock-free port ranges, and optional warm sidecar reuse
// (Azurite, Postgres, ClickHouse) with strict per-lease database/storage/worktree isolation.
internal sealed partial class AgentDevSlotsCandidateHost : ICandidateHost, IAsyncDisposable, IDisposable
{
    public const string CandidateMemoryLimit = "2g";
    public const string CandidateCpuLimit = "2";
    public const string ContainerHomePath = "/home/agent";
    public const string ContainerIntoChatHomePath = "/home/agent/intochat-home";
    public const string LiveRef = CandidateGitWorkspace.LiveRef;
    public const string DevSlotLabelKey = "intochat.devslot";

    private const string AzuriteImage = "mcr.microsoft.com/azure-storage/azurite:3.35.0";
    private const string PostgresImage = "postgres:18.3";
    private const string ClickHouseImage = "clickhouse/clickhouse-server:25.8";
    private const string OsProject = "src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj";
    private const string PostgresAdminDatabase = "postgres";
    private const string OrleansIntegration = "orleans";
    private static readonly TimeSpan DependencyBudget = TimeSpan.FromSeconds(90);

    private static readonly ILogger Log = OsConsoleLog.For<AgentDevSlotsCandidateHost>();

    private readonly IProcessRunner _processes;
    private readonly IConfiguration _live;
    private readonly ICandidateSlotPool _slotPool;
    private readonly CandidateSlotPoolOptions _options;
    private readonly Func<CancellationToken, Task<string>> _workdir;
    private readonly Func<CancellationToken, Task<CandidateSecrets>> _secrets;
    private readonly CandidateGitWorkspace _workspace;
    private readonly ProcessCandidateHost _processFallback;
    private readonly Func<CancellationToken, Task<bool>> _dockerAvailableProbe;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly Func<CandidateSlotLease, CancellationToken, Task> _dependenciesReady;
    private readonly Func<CandidateSlotWarmState, CancellationToken, Task<bool>> _warmHealthProbe;
    private readonly ConcurrentDictionary<string, string> _containerBySha = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _processFallbackShas = new(StringComparer.Ordinal);
    private int _disposed;

    public AgentDevSlotsCandidateHost(
        IModuleIntegrations<AspireModule> integrations,
        IConfiguration configuration,
        IProcessRunner processes,
        ICandidateSlotPool slotPool,
        IOptions<AspireOptions> options,
        ProcessCandidateHost processFallback)
        : this(
            processes,
            configuration,
            slotPool,
            ct => Workdir(integrations, ct),
            ct => Secrets(integrations, ct),
            http: null,
            options: options?.Value.ToSlotPoolOptions(),
            dependenciesReady: null,
            warmHealthProbe: null,
            processFallback: processFallback ?? throw new ArgumentNullException(nameof(processFallback)),
            dockerAvailableProbe: null)
    {
    }

    internal AgentDevSlotsCandidateHost(
        IProcessRunner processes,
        IConfiguration configuration,
        ICandidateSlotPool slotPool,
        Func<CancellationToken, Task<string>> workdir,
        Func<CancellationToken, Task<CandidateSecrets>>? secrets = null,
        HttpClient? http = null,
        CandidateSlotPoolOptions? options = null,
        Func<CandidateSlotLease, CancellationToken, Task>? dependenciesReady = null,
        Func<CandidateSlotWarmState, CancellationToken, Task<bool>>? warmHealthProbe = null,
        ProcessCandidateHost? processFallback = null,
        Func<CancellationToken, Task<bool>>? dockerAvailableProbe = null)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _live = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _slotPool = slotPool ?? throw new ArgumentNullException(nameof(slotPool));
        _workdir = workdir ?? throw new ArgumentNullException(nameof(workdir));
        _secrets = secrets ?? (_ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>())));
        _workspace = new CandidateGitWorkspace(_processes, _workdir);
        _options = options ?? new CandidateSlotPoolOptions();
        _options.Validate();
        if (http is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = http;
            _ownsHttpClient = false;
        }

        _processFallback = processFallback is not null
            ? processFallback
            : CreateProcessFallback(_processes, _workdir, _secrets, _http, _live);
        _dockerAvailableProbe = dockerAvailableProbe ?? ProbeDockerDaemonAsync;
        _dependenciesReady = dependenciesReady ?? WaitForDependenciesReadyAsync;
        _warmHealthProbe = warmHealthProbe ?? ProbeWarmSidecarsHealthyAsync;
    }

    public IReadOnlyDictionary<string, string> LastRunEnvironment { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyList<string> LastRunArguments { get; private set; } = [];
    public int WarmReuseHits => Volatile.Read(ref _warmReuseHits);
    public int ColdStarts => Volatile.Read(ref _coldStarts);
    internal int ProcessFallbackCount => Volatile.Read(ref _processFallbackCount);
    internal bool IsUsingProcessFallback(string sha) => _processFallbackShas.ContainsKey(sha);

    private int _warmReuseHits;
    private int _coldStarts;
    private int _processFallbackCount;

    public Task<PreparedCandidate> Prepare(
        string baseSha,
        string summary,
        IReadOnlyDictionary<string, string> files,
        CancellationToken ct)
        => _workspace.Prepare(baseSha, summary, files, ct);

    public async Task BuildGate(string sha, CancellationToken ct)
    {
        Checked(sha);
        var root = await _workdir(ct).ConfigureAwait(false);
        var worktree = WorktreeOf(root, sha);
        string[] changed;
        try
        {
            changed = await _workspace.ChangedFiles(root, worktree, "HEAD~1", ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException error)
        {
            throw new CandidateHostException("gate", error.Message);
        }

        var projects = changed
            .Select(file => ProjectOf(worktree, file))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var project in projects)
        {
            await _workspace.Dotnet("gate", worktree, ct, "build", project).ConfigureAwait(false);
        }

        var tests = projects
            .Select(project => Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(project)!)!, "tests"))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetDirectories(folder).Where(dir => Directory.EnumerateFiles(dir, "*.csproj").Any()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var test in tests)
        {
            await _workspace.Dotnet("gate", worktree, ct, "test", "--project", test).ConfigureAwait(false);
        }
    }

    public async Task<string> Publish(string sha, CancellationToken ct)
    {
        Checked(sha);
        if (await ShouldUseProcessFallbackAsync(sha, "publish", ct).ConfigureAwait(false))
        {
            return await _processFallback.Publish(sha, ct).ConfigureAwait(false);
        }

        var root = await _workdir(ct).ConfigureAwait(false);
        var worktree = WorktreeOf(root, sha);
        var build = Path.Combine(root, "builds", sha);
        var output = Path.Combine(build, "os");
        var dockerfile = Path.Combine(root, "Candidate.devslot.Dockerfile");
        await _workspace.Dotnet("publish", worktree, ct, "publish", OsProject, "-c", "Release", "-o", output).ConfigureAwait(false);
        var baseImage = (await _workspace.Dotnet(
            "publish",
            worktree,
            ct,
            "msbuild",
            OsProject,
            "-t:ComputeContainerBaseImage",
            "-p:Configuration=Release",
            "-p:EnableSdkContainerSupport=true",
            "-p:RuntimeIdentifier=linux-x64",
            "-getProperty:ContainerBaseImage").ConfigureAwait(false)).Trim();
        if (!ImageReference().IsMatch(baseImage))
        {
            throw new CandidateHostException("publish", $"{OsProject} returned an invalid ContainerBaseImage");
        }

        // Pin LF line endings and pre-create unprivileged UID:GID home directories inside the image.
        var dockerfileContent =
            $"FROM {baseImage}\n"
            + "WORKDIR /app\n"
            + "COPY . .\n"
            + $"RUN mkdir -p {ContainerHomePath} {ContainerIntoChatHomePath} && chown -R {_options.UserSpec} {ContainerHomePath}\n"
            + $"USER {_options.UserSpec}\n"
            + "ENTRYPOINT [\"dotnet\", \"IntoChat.OS.Kernel.dll\"]\n";
        await File.WriteAllTextAsync(dockerfile, dockerfileContent, ct).ConfigureAwait(false);
        await Docker("publish", ct, "build", "-t", ImageOf(sha), "-f", dockerfile, output).ConfigureAwait(false);
        return build;
    }

    public async Task<RunningCandidate> Run(string sha, CancellationToken ct)
    {
        Checked(sha);
        if (await ShouldUseProcessFallbackAsync(sha, "run", ct).ConfigureAwait(false))
        {
            return await _processFallback.Run(sha, ct).ConfigureAwait(false);
        }

        var root = await _workdir(ct).ConfigureAwait(false);
        var given = await _secrets(ct).ConfigureAwait(false);
        var lease = await _slotPool.AcquireAsync(sha, root, ct: ct).ConfigureAwait(false);
        var containerName = CandidateContainerOf(lease.SlotId, sha);
        var sidecarsHealthy = false;
        try
        {
            var warmState = await EnsureSidecarsReadyAsync(lease, ct).ConfigureAwait(false);
            sidecarsHealthy = true;

            // Even when warm sidecars are reused, each lease gets freshly created isolated databases.
            await ProvisionLeaseDatabasesAsync(lease, warmState, ct).ConfigureAwait(false);

            var environment = BuildCandidateEnvironment(lease, warmState, given.Model);
            var runArgs = BuildCandidateRunArguments(lease, warmState, containerName, sha, environment);
            LastRunEnvironment = environment;
            LastRunArguments = runArgs;

            await Container("run", ct, environment, runArgs).ConfigureAwait(false);
            _containerBySha[sha] = containerName;
            var endpoint = $"http://localhost:{lease.Ports.HttpPort}";
            return new RunningCandidate(sha, new CandidateEndpoints(endpoint, endpoint), containerName);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Candidate {Sha} failed during dev-slot Run on {SlotId}; rolling back.", sha, lease.SlotId);
            await RollbackRunAsync(sha, lease, containerName, sidecarsHealthy).ConfigureAwait(false);
            throw;
        }
    }

    public async Task WaitHealthy(string sha, TimeSpan budget, CancellationToken ct)
    {
        Checked(sha);
        if (_processFallbackShas.ContainsKey(sha))
        {
            await _processFallback.WaitHealthy(sha, budget, ct).ConfigureAwait(false);
            return;
        }

        if (!_slotPool.TryGetActiveLease(sha, out var lease) || lease is null)
        {
            throw new CandidateHostException("healthy", $"{sha} is not running on this host");
        }

        var containerName = _containerBySha.TryGetValue(sha, out var tracked)
            ? tracked
            : CandidateContainerOf(lease.SlotId, sha);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(budget);
        try
        {
            var healthUri = new Uri($"http://127.0.0.1:{lease.Ports.HttpPort}/health");
            while (true)
            {
                await EnsureContainerRunningAsync("healthy", containerName, limit.Token).ConfigureAwait(false);
                try
                {
                    using var response = await _http.GetAsync(healthUri, limit.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException ex)
                {
                    Log.LogDebug(ex, "Candidate {Sha} in {SlotId} not answering health yet.", sha, lease.SlotId);
                }
                catch (TaskCanceledException ex) when (!limit.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    Log.LogDebug(ex, "Health probe timed out for candidate {Sha} in {SlotId}; retrying within budget.", sha, lease.SlotId);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{containerName} was not healthy within {budget}.");
        }
    }

    public Task<string?> Commit(string sha, CancellationToken ct)
        => _workspace.Commit(sha, ct);

    public Task Uncommit(string sha, string? previous, CancellationToken ct)
        => _workspace.Uncommit(sha, previous, ct);

    public async Task Remove(string sha, CancellationToken ct)
    {
        Checked(sha);
        if (_processFallbackShas.TryRemove(sha, out _))
        {
            await _processFallback.Remove(sha, ct).ConfigureAwait(false);
            return;
        }

        _slotPool.TryGetActiveLease(sha, out var lease);
        var containerName = _containerBySha.TryRemove(sha, out var tracked)
            ? tracked
            : lease is not null ? CandidateContainerOf(lease.SlotId, sha) : null;

        if (containerName is not null)
        {
            await Ignoring("No such", ct, "rm", "-f", "-v", containerName).ConfigureAwait(false);
        }

        if (lease is null)
        {
            return;
        }

        var preserveWarm = false;
        if (_options.EnableWarmSlotReuse && lease.WarmState is { } warm)
        {
            if (await TryResetLeaseDatabasesAsync(lease, warm, ct).ConfigureAwait(false))
            {
                preserveWarm = true;
            }
            else
            {
                await TeardownSidecarsAsync(warm, ct).ConfigureAwait(false);
                lease.ClearWarmState();
            }
        }
        else if (lease.WarmState is { } coldTearDown)
        {
            await TeardownSidecarsAsync(coldTearDown, ct).ConfigureAwait(false);
            lease.ClearWarmState();
        }

        await lease.ReleaseAsync(preserveWarmDependencies: preserveWarm, ct).ConfigureAwait(false);
    }

    public async Task<string[]> Orphans(string[] known, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (await ShouldUseProcessFallbackAsync(sha: null, "orphans", ct).ConfigureAwait(false))
        {
            var fallbackOrphans = await _processFallback.Orphans(known, ct).ConfigureAwait(false);
            foreach (var sha in fallbackOrphans)
            {
                _processFallbackShas.TryRemove(sha, out _);
            }

            return fallbackOrphans;
        }

        var listed = await _processes.RunAsync(
            "docker",
            ["ps", "-a", "--filter", $"label={DevSlotLabelKey}=true", "--format", "{{.Names}}"],
            ".",
            null,
            ct).ConfigureAwait(false);
        if (listed.ExitCode != 0)
        {
            throw new CandidateHostException("orphans", Tail(listed));
        }

        var removed = new List<string>();
        foreach (var name in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = DevSlotCandidateName().Match(name);
            if (!match.Success)
            {
                continue;
            }

            var sha = match.Groups["sha"].Value;
            if (known.Contains(sha, StringComparer.Ordinal))
            {
                continue;
            }

            await Ignoring("No such", ct, "rm", "-f", "-v", name).ConfigureAwait(false);
            removed.Add(name);
            if (_slotPool.TryGetActiveLease(sha, out _))
            {
                await _slotPool.ReleaseAsync(sha, preserveWarmDependencies: false, ct).ConfigureAwait(false);
            }
        }

        if (!_processFallbackShas.IsEmpty)
        {
            var fallbackOrphans = await _processFallback.Orphans(known, ct).ConfigureAwait(false);
            foreach (var sha in fallbackOrphans)
            {
                _processFallbackShas.TryRemove(sha, out _);
                removed.Add(sha);
            }
        }

        return [.. removed];
    }

    // Proactively warms up to `count` slots so subsequent candidate tournaments acquire warm slots immediately.
    public async Task<int> PreWarmSlotsAsync(int count, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (!_options.EnableWarmSlotReuse)
        {
            return 0;
        }

        if (!await _dockerAvailableProbe(ct).ConfigureAwait(false))
        {
            Log.LogWarning("Docker daemon is unavailable; skipping pre-warming of {Count} dev slot(s).", count);
            return 0;
        }

        var target = Math.Min(count, _slotPool.Capacity);
        var root = await _workdir(ct).ConfigureAwait(false);
        var warmedLeases = new List<CandidateSlotLease>(target);
        try
        {
            for (var i = 0; i < target; i++)
            {
                var prewarmOwner = $"prewarm-{Guid.NewGuid():N}";
                var lease = await _slotPool.AcquireAsync(prewarmOwner, root, ct: ct).ConfigureAwait(false);
                warmedLeases.Add(lease);
                await EnsureSidecarsReadyAsync(lease, ct).ConfigureAwait(false);
            }

            return warmedLeases.Count;
        }
        finally
        {
            foreach (var lease in warmedLeases)
            {
                await lease.ReleaseAsync(preserveWarmDependencies: lease.IsWarm, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    // Tears down all warm sidecar stacks currently pooled across idle slots.
    public async Task DrainWarmSlotsAsync(CancellationToken ct = default)
    {
        var warmStates = _slotPool.DrainWarmStates();
        foreach (var warm in warmStates)
        {
            await TeardownSidecarsAsync(warm, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await DrainWarmSlotsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Best-effort warm slot drain failed during AgentDevSlotsCandidateHost disposal.");
        }

        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    internal Dictionary<string, string> BuildCandidateEnvironment(
        CandidateSlotLease lease,
        CandidateSlotWarmState warm,
        IReadOnlyDictionary<string, string> modelSecrets)
    {
        var environment = CandidateGitWorkspace.CopySafeHostConfiguration(_live, rewriteLoopbackForContainer: true);

        // Apply only explicit CandidateSecrets (AI model keys).
        foreach (var (key, value) in modelSecrets)
        {
            if (ChildEnvironmentPolicy.IsHostCustodySecret(key)
                || string.Equals(key, PresenceProof.TokenHashEnvironmentVariable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            environment[key] = value;
        }

        var leaseShort = lease.LeaseToken.Length >= 6 ? lease.LeaseToken[..6] : lease.LeaseToken;
        var orleansId = $"os-{lease.SlotId}-{lease.OwnerSha}-{leaseShort}";
        var azuriteConnection = SlotAzuriteConnectionString(warm.AzuriteContainer);

        var overrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ASPNETCORE_URLS"] = "http://+:5080",
            ["HOME"] = ContainerHomePath,
            ["XDG_CONFIG_HOME"] = $"{ContainerHomePath}/.config",
            ["XDG_DATA_HOME"] = $"{ContainerHomePath}/.local/share",
            ["XDG_CACHE_HOME"] = $"{ContainerHomePath}/.cache",
            ["TMPDIR"] = $"{ContainerHomePath}/tmp",
            ["DOTNET_CLI_HOME"] = ContainerHomePath,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null",
            ["Orleans__ServiceId"] = orleansId,
            ["Orleans__ClusterId"] = orleansId,
            ["Orleans__EnableDistributedTracing"] = "true",
            [CandidateGitWorkspace.IntegrationVariable(OrleansIntegration, "Clustering")] = azuriteConnection,
            [CandidateGitWorkspace.IntegrationVariable(OrleansIntegration, "GrainState")] = azuriteConnection,
            [CandidateGitWorkspace.IntegrationVariable(OrleansIntegration, "Reminders")] = azuriteConnection,
            [CandidateGitWorkspace.IntegrationVariable(OrleansIntegration, "GrainStateContainer")] = lease.GrainStateContainerName,
            [ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleOptionsSection("apps") + ":Features")] = "/app/features",
            ["OTEL_SERVICE_NAME"] = $"devslot-{lease.SlotId}-{lease.OwnerSha}",
            [CandidateGitWorkspace.Env(IntoChatOSNames.HomeKey)] = ContainerIntoChatHomePath,
            [CandidateGitWorkspace.Env(IntoChatOSNames.ConfiguredCustodyAcknowledgedConfigurationKey)] = "true",
            [IntoChatOSNames.MasterKeyEnvironmentVariable] = Secret(),
            [CandidateGitWorkspace.IntegrationVariable("postgres", "ConnectionString")] =
                $"Host={warm.PostgresContainer};Port=5432;Username=postgres;Password={warm.PostgresPassword};Database={lease.PostgresDatabaseName}",
            [CandidateGitWorkspace.IntegrationVariable("postgres", "AdminConnection")] =
                $"Host={warm.PostgresContainer};Port=5432;Username=postgres;Password={warm.PostgresPassword}",
            [CandidateGitWorkspace.IntegrationVariable("supabase", "ConnectionString")] =
                $"Host={warm.PostgresContainer};Port=5432;Username=postgres;Password={warm.PostgresPassword};Database={lease.PostgresDatabaseName}",
            [CandidateGitWorkspace.IntegrationVariable("clickhouse", "ConnectionString")] =
                $"Host={warm.ClickHouseContainer};Port=8123;Protocol=http;Username=default;Password={warm.ClickHousePassword};Database={lease.ClickHouseDatabaseName}",
        };

        foreach (var (key, value) in overrides)
        {
            environment[key] = value;
        }

        // Foundry Local runs on the developer workstation only; drop it for Linux containers.
        environment.Remove(ModuleConfiguration.EnvironmentName(ModuleConfiguration.ModuleOptionsSection("ai") + ":Default:Transcription"));
        return environment;
    }

    internal static string[] BuildCandidateRunArguments(
        CandidateSlotLease lease,
        CandidateSlotWarmState warm,
        string containerName,
        string sha,
        IReadOnlyDictionary<string, string> environment)
    {
        var args = new List<string>(32 + (environment.Count * 2))
        {
            "run", "-d",
            "--name", containerName,
            "--hostname", containerName,
            "--network", warm.NetworkName,
            "--user", lease.UserSpec,
            "--security-opt", "no-new-privileges:true",
            "--cap-drop", "ALL",
            "--memory", CandidateMemoryLimit,
            "--cpus", CandidateCpuLimit,
            "-v", $"{lease.EphemeralHome}:{ContainerHomePath}",
            "-p", $"127.0.0.1:{lease.Ports.HttpPort}:5080",
            "--add-host", "host.docker.internal:host-gateway",
            "--label", $"{DevSlotLabelKey}=true",
            "--label", $"{DevSlotLabelKey}.slot={lease.SlotId}",
            "--label", $"{DevSlotLabelKey}.sha={sha}",
            "--label", $"{DevSlotLabelKey}.lease={lease.LeaseId}",
        };

        // Pass only variable names via `-e KEY`; values travel via docker CLI process environment, never CLI args.
        foreach (var key in environment.Keys.Order(StringComparer.Ordinal))
        {
            args.Add("-e");
            args.Add(key);
        }

        args.Add(ImageOf(sha));
        return [.. args];
    }

    private async Task<CandidateSlotWarmState> EnsureSidecarsReadyAsync(CandidateSlotLease lease, CancellationToken ct)
    {
        if (_options.EnableWarmSlotReuse && lease.WarmState is { } existingWarm)
        {
            if (await _warmHealthProbe(existingWarm, ct).ConfigureAwait(false))
            {
                var reused = existingWarm.NextReuse();
                lease.AttachWarmState(reused);
                Interlocked.Increment(ref _warmReuseHits);
                return reused;
            }

            Log.LogWarning("Warm sidecars for slot {SlotId} were unhealthy; recycling cold.", lease.SlotId);
            await TeardownSidecarsAsync(existingWarm, CancellationToken.None).ConfigureAwait(false);
            lease.ClearWarmState();
        }

        Interlocked.Increment(ref _coldStarts);
        var network = NetworkOf(lease.SlotId);
        var azurite = AzuriteOf(lease.SlotId);
        var postgres = PostgresOf(lease.SlotId);
        var clickHouse = ClickHouseOf(lease.SlotId);
        var postgresPassword = Secret();
        var clickHousePassword = Secret();

        await Docker("run", ct, "network", "create", network).ConfigureAwait(false);
        await Docker(
            "run",
            ct,
            "run", "-d",
            "--name", azurite,
            "--network", network,
            "--label", $"{DevSlotLabelKey}=true",
            "--label", $"{DevSlotLabelKey}.role=sidecar",
            "--label", $"{DevSlotLabelKey}.slot={lease.SlotId}",
            "-p", $"127.0.0.1:{lease.Ports.AzuriteBlobPort}:10000",
            "-p", $"127.0.0.1:{lease.Ports.AzuriteTablePort}:10002",
            AzuriteImage,
            "azurite", "--blobHost", "0.0.0.0", "--tableHost", "0.0.0.0", "--skipApiVersionCheck", "--loose").ConfigureAwait(false);

        await Container(
            "run",
            ct,
            new Dictionary<string, string>
            {
                ["POSTGRES_PASSWORD"] = postgresPassword,
                ["POSTGRES_DB"] = PostgresAdminDatabase,
            },
            "run", "-d",
            "--name", postgres,
            "--network", network,
            "--label", $"{DevSlotLabelKey}=true",
            "--label", $"{DevSlotLabelKey}.role=sidecar",
            "--label", $"{DevSlotLabelKey}.slot={lease.SlotId}",
            "-p", $"127.0.0.1:{lease.Ports.PostgresPort}:5432",
            "--health-cmd", "pg_isready -U postgres",
            "--health-interval", "1s",
            "-e", "POSTGRES_PASSWORD",
            "-e", "POSTGRES_DB",
            PostgresImage).ConfigureAwait(false);

        await Container(
            "run",
            ct,
            new Dictionary<string, string>
            {
                ["CLICKHOUSE_USER"] = "default",
                ["CLICKHOUSE_PASSWORD"] = clickHousePassword,
            },
            "run", "-d",
            "--name", clickHouse,
            "--network", network,
            "--label", $"{DevSlotLabelKey}=true",
            "--label", $"{DevSlotLabelKey}.role=sidecar",
            "--label", $"{DevSlotLabelKey}.slot={lease.SlotId}",
            "-p", $"127.0.0.1:{lease.Ports.ClickHousePort}:8123",
            "--health-cmd", "clickhouse-client --query 'SELECT 1'",
            "--health-interval", "1s",
            "-e", "CLICKHOUSE_USER",
            "-e", "CLICKHOUSE_PASSWORD",
            ClickHouseImage).ConfigureAwait(false);

        var warmState = new CandidateSlotWarmState(
            network,
            azurite,
            postgres,
            clickHouse,
            postgresPassword,
            clickHousePassword,
            ReuseCount: 0);
        lease.AttachWarmState(warmState);

        await _dependenciesReady(lease, ct).ConfigureAwait(false);
        return warmState;
    }

    private async Task ProvisionLeaseDatabasesAsync(CandidateSlotLease lease, CandidateSlotWarmState warm, CancellationToken ct)
    {
        await Container(
            "run",
            ct,
            new Dictionary<string, string> { ["PGPASSWORD"] = warm.PostgresPassword },
            "exec", "-e", "PGPASSWORD", warm.PostgresContainer,
            "psql", "-U", "postgres", "-d", PostgresAdminDatabase, "-c", $"CREATE DATABASE \"{lease.PostgresDatabaseName}\"").ConfigureAwait(false);

        await Container(
            "run",
            ct,
            new Dictionary<string, string> { ["CLICKHOUSE_PASSWORD"] = warm.ClickHousePassword },
            "exec", "-e", "CLICKHOUSE_PASSWORD", warm.ClickHouseContainer,
            "clickhouse-client", "--user", "default", "--query", $"CREATE DATABASE IF NOT EXISTS {lease.ClickHouseDatabaseName}").ConfigureAwait(false);
    }

    private async Task<bool> TryResetLeaseDatabasesAsync(CandidateSlotLease lease, CandidateSlotWarmState warm, CancellationToken ct)
    {
        try
        {
            var pgResult = await _processes.RunAsync(
                "docker",
                [
                    "exec", "-e", "PGPASSWORD", warm.PostgresContainer,
                    "psql", "-U", "postgres", "-d", PostgresAdminDatabase,
                    "-c", $"DROP DATABASE IF EXISTS \"{lease.PostgresDatabaseName}\" WITH (FORCE)",
                ],
                ".",
                new Dictionary<string, string> { ["PGPASSWORD"] = warm.PostgresPassword },
                ct).ConfigureAwait(false);
            if (pgResult.ExitCode != 0)
            {
                Log.LogWarning("Failed to drop lease Postgres DB {Db} on {SlotId}: {Detail}", lease.PostgresDatabaseName, lease.SlotId, Tail(pgResult));
                return false;
            }

            var chResult = await _processes.RunAsync(
                "docker",
                [
                    "exec", "-e", "CLICKHOUSE_PASSWORD", warm.ClickHouseContainer,
                    "clickhouse-client", "--user", "default",
                    "--query", $"DROP DATABASE IF EXISTS {lease.ClickHouseDatabaseName}",
                ],
                ".",
                new Dictionary<string, string> { ["CLICKHOUSE_PASSWORD"] = warm.ClickHousePassword },
                ct).ConfigureAwait(false);
            if (chResult.ExitCode != 0)
            {
                Log.LogWarning("Failed to drop lease ClickHouse DB {Db} on {SlotId}: {Detail}", lease.ClickHouseDatabaseName, lease.SlotId, Tail(chResult));
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogWarning(ex, "Warm slot database reset failed for {SlotId}; tearing down sidecars.", lease.SlotId);
            return false;
        }
    }

    private async Task<bool> ProbeWarmSidecarsHealthyAsync(CandidateSlotWarmState warm, CancellationToken ct)
    {
        foreach (var container in new[] { warm.AzuriteContainer, warm.PostgresContainer, warm.ClickHouseContainer })
        {
            var state = await _processes.RunAsync("docker", ["inspect", "-f", "{{.State.Running}}", container], ".", null, ct).ConfigureAwait(false);
            if (state.ExitCode != 0 || !state.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private async Task WaitForDependenciesReadyAsync(CandidateSlotLease lease, CancellationToken ct)
    {
        var warm = lease.WarmState ?? throw new InvalidOperationException("WarmState must be attached before waiting for dependencies.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DependencyBudget);
        try
        {
            while (true)
            {
                await EnsureContainerRunningAsync("run", warm.AzuriteContainer, limit.Token).ConfigureAwait(false);
                try
                {
                    using var response = await _http.GetAsync(
                        new Uri($"http://127.0.0.1:{lease.Ports.AzuriteBlobPort}/{CandidateStorage.AccountName}?comp=properties&restype=service"),
                        limit.Token).ConfigureAwait(false);
                    break;
                }
                catch (HttpRequestException ex)
                {
                    Log.LogDebug(ex, "Waiting for slot {SlotId} Azurite to answer on port {Port}.", lease.SlotId, lease.Ports.AzuriteBlobPort);
                    await Task.Delay(TimeSpan.FromMilliseconds(200), limit.Token).ConfigureAwait(false);
                }
            }

            foreach (var container in new[] { warm.PostgresContainer, warm.ClickHouseContainer })
            {
                while (true)
                {
                    await EnsureContainerRunningAsync("run", container, limit.Token).ConfigureAwait(false);
                    var health = await _processes.RunAsync(
                        "docker",
                        ["inspect", "-f", "{{.State.Health.Status}}", container],
                        ".",
                        null,
                        limit.Token).ConfigureAwait(false);
                    if (health.ExitCode == 0 && health.Output.Trim() == "healthy")
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(350), limit.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CandidateHostException("run", $"slot {lease.SlotId} storage and databases were not ready within {DependencyBudget}.");
        }
    }

    private async Task RollbackRunAsync(string sha, CandidateSlotLease lease, string containerName, bool sidecarsHealthy)
    {
        _containerBySha.TryRemove(sha, out _);
        try
        {
            await Ignoring("No such", CancellationToken.None, "rm", "-f", "-v", containerName).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Best-effort candidate container cleanup failed during rollback for {Sha}.", sha);
        }

        var preserveWarm = false;
        if (sidecarsHealthy && _options.EnableWarmSlotReuse && lease.WarmState is { } warm)
        {
            preserveWarm = await TryResetLeaseDatabasesAsync(lease, warm, CancellationToken.None).ConfigureAwait(false);
            if (!preserveWarm)
            {
                await TeardownSidecarsSafeAsync(warm).ConfigureAwait(false);
                lease.ClearWarmState();
            }
        }
        else if (lease.WarmState is { } failedSidecars)
        {
            await TeardownSidecarsSafeAsync(failedSidecars).ConfigureAwait(false);
            lease.ClearWarmState();
        }
        else
        {
            // Clean up any partial cold sidecars or network created before WarmState was attached.
            var partial = new CandidateSlotWarmState(
                NetworkOf(lease.SlotId),
                AzuriteOf(lease.SlotId),
                PostgresOf(lease.SlotId),
                ClickHouseOf(lease.SlotId),
                "",
                "");
            await TeardownSidecarsSafeAsync(partial).ConfigureAwait(false);
        }

        await lease.ReleaseAsync(preserveWarmDependencies: preserveWarm, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TeardownSidecarsAsync(CandidateSlotWarmState warm, CancellationToken ct)
    {
        await Ignoring("No such", ct, "rm", "-f", "-v", warm.AzuriteContainer, warm.PostgresContainer, warm.ClickHouseContainer).ConfigureAwait(false);
        await Ignoring("not found", ct, "network", "rm", warm.NetworkName).ConfigureAwait(false);
    }

    private async Task TeardownSidecarsSafeAsync(CandidateSlotWarmState warm)
    {
        try
        {
            await TeardownSidecarsAsync(warm, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Best-effort sidecar teardown failed for network {Network}.", warm.NetworkName);
        }
    }

    private async Task<bool> ShouldUseProcessFallbackAsync(string? sha, string stage, CancellationToken ct)
    {
        if (sha is not null && _processFallbackShas.ContainsKey(sha))
        {
            return true;
        }

        if (await _dockerAvailableProbe(ct).ConfigureAwait(false))
        {
            return false;
        }

        Log.LogWarning(
            "Docker daemon is unavailable during candidate stage '{Stage}' for '{Sha}'; falling back automatically to ProcessCandidateHost.",
            stage,
            sha ?? "<orphans>");
        if (sha is not null)
        {
            _processFallbackShas[sha] = 0;
        }

        Interlocked.Increment(ref _processFallbackCount);
        return true;
    }

    private async Task<bool> ProbeDockerDaemonAsync(CancellationToken ct)
    {
        try
        {
            var result = await _processes.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], ".", null, ct).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LogDebug(ex, "Docker daemon availability probe failed with exception.");
            return false;
        }
    }

    private async Task EnsureContainerRunningAsync(string stage, string container, CancellationToken ct)
    {
        var state = await _processes.RunAsync("docker", ["inspect", "-f", "{{.State.Running}}", container], ".", null, ct).ConfigureAwait(false);
        if (state.ExitCode == 0 && state.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var logs = await _processes.RunAsync("docker", ["logs", "--tail", "40", container], ".", null, CancellationToken.None).ConfigureAwait(false);
        throw new CandidateHostException(stage, $"{container} stopped: {Tail(logs)}");
    }

    private static string? ProjectOf(string worktree, string file)
    {
        var full = Path.GetFullPath(worktree);
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(full, file))); dir is not null && dir.Length > full.Length; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csproj").FirstOrDefault() is { } project)
            {
                return project;
            }
        }

        return null;
    }

    private Task Docker(string stage, CancellationToken ct, params string[] args)
        => Container(stage, ct, null, args);

    private async Task Container(string stage, CancellationToken ct, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var result = await _processes.RunAsync("docker", args, ".", environment, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new CandidateHostException(stage, $"docker {string.Join(' ', args)}:\n{Tail(result)}");
        }
    }

    private async Task Ignoring(string absent, CancellationToken ct, params string[] args)
    {
        var result = await _processes.RunAsync("docker", args, ".", null, ct).ConfigureAwait(false);
        var errors = result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (result.ExitCode != 0 && (errors.Length == 0 || !errors.All(line => line.Contains(absent, StringComparison.OrdinalIgnoreCase))))
        {
            throw new CandidateHostException("remove", $"docker {string.Join(' ', args)}:\n{Tail(result)}");
        }
    }

    private static string SlotAzuriteConnectionString(string azuriteContainer)
        => $"DefaultEndpointsProtocol=http;AccountName={CandidateStorage.AccountName};AccountKey={CandidateStorage.AccountKey};"
            + $"BlobEndpoint=http://{azuriteContainer}:10000/{CandidateStorage.AccountName};TableEndpoint=http://{azuriteContainer}:10002/{CandidateStorage.AccountName};";

    private static ProcessCandidateHost CreateProcessFallback(
        IProcessRunner processes,
        Func<CancellationToken, Task<string>> workdir,
        Func<CancellationToken, Task<CandidateSecrets>> secrets,
        HttpClient http,
        IConfiguration live)
        => new(processes, workdir, secrets, launcher: null, http: http, portAllocator: null, configuration: live);

    private static string ImageOf(string sha) => $"intochat-os-candidate:{sha}";
    internal static string CandidateContainerOf(string slotId, string sha) => $"devslot-{slotId}-candidate-{sha}";
    internal static string AzuriteOf(string slotId) => $"devslot-{slotId}-azurite";
    internal static string PostgresOf(string slotId) => $"devslot-{slotId}-postgres";
    internal static string ClickHouseOf(string slotId) => $"devslot-{slotId}-clickhouse";
    internal static string NetworkOf(string slotId) => $"devslot-{slotId}";

    [GeneratedRegex("^devslot-(?<slot>slot-[0-9]{2})-candidate-(?<sha>[0-9a-f]{7,40})$", RegexOptions.CultureInvariant)]
    private static partial Regex DevSlotCandidateName();

    [GeneratedRegex("\\A[a-zA-Z0-9][a-zA-Z0-9._:/@-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImageReference();
}
