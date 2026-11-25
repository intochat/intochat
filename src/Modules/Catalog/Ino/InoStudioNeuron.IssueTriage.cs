using System.Globalization;
using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Caching;
using DigitalBrain.Contracts;
using DigitalBrain.Log;
using DigitalBrain.Rules;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Microsoft.GitHub.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

internal sealed record IssueTriageBindingEntry(
    string Slug,
    string UnitAlias,
    string ConnectionKey,
    string Mode,
    bool Enabled = true,
    string[]? ProcessedIssueVersions = null,
    int TriagedCount = 0,
    int LastIssueNumber = 0,
    string LastKind = "",
    string LastReceipt = "");

internal sealed record InoStudioState(IssueTriageBindingEntry[]? IssueTriageBindings = null)
{
    public InoStudioState() : this([]) { }
}

[DigitalBrain.Signal("ino.github-triage.configured")]
internal sealed record IssueTriageConfigured(string UnitAlias, string ConnectionKey, string Mode) : DigitalBrain.Signal;

[DigitalBrain.Signal("gh.triage.recorded")]
internal sealed record IssueTriageRecorded(
    string UnitAlias,
    int Number,
    string Version,
    string Kind,
    string Mode,
    string Summary,
    string Receipt) : DigitalBrain.Signal;

[DigitalBrain.Signal("gh.triage.blocked")]
internal sealed record IssueTriageBlocked(
    string UnitAlias,
    int Number,
    string Version,
    string Reason) : DigitalBrain.Signal;

internal sealed partial class InoStudioNeuron
{
    internal const int MaxProcessedIssueVersions = 256;

    private static readonly BoundedCache<string, bool> VerifiedPackages = new(64, StringComparer.Ordinal);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        await base.OnActivate(cancellationToken).ConfigureAwait(true);
        var bindings = State.IssueTriageBindings ?? [];
        foreach (var entry in bindings)
        {
            if (entry.Enabled && !string.IsNullOrWhiteSpace(entry.ConnectionKey))
            {
                await Follow<IntoChat.OS.Modules.Microsoft.GitHub.IRepository, IntoChat.OS.Modules.Microsoft.GitHub.Signals.IssueChanged>(entry.ConnectionKey)
                    .ConfigureAwait(true);
            }
        }
    }

    public async Task<InoToolResult> CreateIssueTriageSynapse(
        string connectionKey,
        string mode = IssueTriageSynapseGenerator.DraftForApprovalMode,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var currentBrain = CurrentBrainId;

        if (!IssueTriageSynapseGenerator.TryNormalizeConnectionKey(
                currentBrain,
                connectionKey,
                out var normalizedConnectionKey,
                out var slug,
                out var keyError))
        {
            return new InoToolResult(keyError, Ok: false);
        }

        if (!IssueTriageSynapseGenerator.TryNormalizeMode(mode, out var normalizedMode, out var modeError))
        {
            return new InoToolResult(modeError, Ok: false);
        }

        var package = IssueTriageSynapseGenerator.Generate(slug, normalizedConnectionKey, normalizedMode);
        var studio = Get<ISynapseStudio>(ISynapseStudio.Key(currentBrain));

        Dictionary<string, string>? existingSidecars = null;
        try
        {
            var sidecars = await studio.Sidecars(package.Alias).ConfigureAwait(true);
            if (sidecars.Count > 0)
            {
                existingSidecars = sidecars;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioLogger.LogSwallowed(ex, $"reading existing sidecars for '{package.Alias}'");
        }

        var verifier = services.GetRequiredService<IUnitVerifier>();
        IReadOnlyList<Scenario> mergedScenarios;
        try
        {
            mergedScenarios = verifier.Keep(
                existingSidecars,
                package.Scenarios,
                $"Install {package.Alias} ({normalizedMode})");
        }
        catch (InvalidDataException ex)
        {
            StudioLogger.LogSwallowed(ex, $"merging kept scenarios for '{package.Alias}'");
            mergedScenarios = package.Scenarios;
        }

        var files = new Dictionary<string, string>(package.Files, StringComparer.Ordinal)
        {
            [UnitFiles.Scenarios] = JsonSerializer.Serialize(
                mergedScenarios,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
        };

        var verificationKey = ISynapseStudio.StepKey(package.Alias, files);
        if (!VerifiedPackages.TryGet(verificationKey, out var alreadyVerified) || !alreadyVerified)
        {
            var verified = await verifier.Verify(
                package.Alias,
                files,
                mergedScenarios,
                ct).ConfigureAwait(true);
            if (!verified.Passed)
            {
                var details = string.Join(
                    "\n",
                    verified.Diagnostics.Select(d => $"{d.File}({d.Line}) {d.Id}: {d.Message}").Concat(verified.Failures));
                return new InoToolResult(
                    $"Generated triage synapse '{package.Alias}' failed verification:\n{details}",
                    Ok: false);
            }

            VerifiedPackages.Set(verificationKey, true);
        }

        UnitDeployResult deployed;
        try
        {
            deployed = await studio.DeployFiles(
                package.Alias,
                files,
                new Dictionary<string, string>(StringComparer.Ordinal)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioLogger.LogSwallowed(ex, $"deploying triage synapse '{package.Alias}'");
            return new InoToolResult($"Could not deploy triage synapse '{package.Alias}': {ex.Message}", Ok: false);
        }

        if (!deployed.Ok)
        {
            return new InoToolResult(deployed.Text, Ok: false);
        }

        var currentBindings = State.IssueTriageBindings ?? [];
        var existingBinding = currentBindings.FirstOrDefault(b =>
            string.Equals(b.ConnectionKey, normalizedConnectionKey, StringComparison.Ordinal));
        var updatedEntry = existingBinding is not null
            ? existingBinding with
            {
                Slug = slug,
                UnitAlias = package.Alias,
                ConnectionKey = normalizedConnectionKey,
                Mode = normalizedMode,
                Enabled = true,
            }
            : new IssueTriageBindingEntry(
                Slug: slug,
                UnitAlias: package.Alias,
                ConnectionKey: normalizedConnectionKey,
                Mode: normalizedMode,
                Enabled: true,
                ProcessedIssueVersions: []);

        var nextBindings = currentBindings
            .Where(b => !string.Equals(b.ConnectionKey, normalizedConnectionKey, StringComparison.Ordinal))
            .Append(updatedEntry)
            .ToArray();

        await Save(
            State with { IssueTriageBindings = nextBindings },
            new IssueTriageConfigured(package.Alias, normalizedConnectionKey, normalizedMode)).ConfigureAwait(true);

        await Follow<IntoChat.OS.Modules.Microsoft.GitHub.IRepository, IntoChat.OS.Modules.Microsoft.GitHub.Signals.IssueChanged>(normalizedConnectionKey)
            .ConfigureAwait(true);

        var creatorUri = $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(Id.Brain, "creator").Key}#{package.Alias}";
        var tasksUri = $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(Id.Brain, "tasks").Key}#approvals";
        var install = deployed.InstallOf(package.Alias);
        var cardStatus = UnitDeployResult.IsLive(install.Status) ? "installed" : install.Status.ToString().ToLowerInvariant();

        var card = new InoCard(
            Id: package.Alias,
            Kind: "synapse",
            Title: $"Issue Triage Synapse · {package.Alias} ({normalizedMode})",
            Summary: $"Subscribed to {normalizedConnectionKey} · Inspect in Creator ({creatorUri}) · Review drafts in Tasks ({tasksUri})",
            SourceNeuronUri: creatorUri,
            Status: cardStatus,
            SurfaceScopeUri: tasksUri);

        var summary = $"Installed GitHub issue triage synapse '{package.Alias}' for `{normalizedConnectionKey}` in `{normalizedMode}` mode (hash `{deployed.Hash}`). Inspect live C# source, scenarios, and 3D state graph in Creator ({creatorUri}) or review triage drafts in Tasks ({tasksUri}).";
        return new InoToolResult(summary, card, Ok: true);
    }

    [DigitalBrain.Neurons.On]
    public async Task OnIssueChanged(IssueChanged signal, CancellationToken ct)
    {
        if (signal is null || signal.Number <= 0 || signal.Action is not ("opened" or "reopened"))
        {
            return;
        }

        var activeBindings = (State.IssueTriageBindings ?? [])
            .Where(b => b.Enabled && !string.IsNullOrWhiteSpace(b.ConnectionKey))
            .ToArray();
        if (activeBindings.Length == 0)
        {
            return;
        }

        var version = signal.Version ?? string.Empty;
        var versionKey = string.Create(CultureInfo.InvariantCulture, $"{signal.Number}:{version}");

        foreach (var binding in activeBindings)
        {
            var processed = binding.ProcessedIssueVersions ?? [];
            if (processed.Contains(versionKey, StringComparer.Ordinal))
            {
                continue;
            }

            var repo = Get<IRepository>(new NeuronId(Id.Brain, "github", binding.ConnectionKey));
            IssueRead issueRead;
            try
            {
                issueRead = await repo.ReadIssue(new ReadIssue(signal.Number), ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StudioLogger.LogSwallowed(ex, $"reading GitHub issue #{signal.Number} on '{binding.ConnectionKey}'");
                continue;
            }

            if (activeBindings.Length > 1
                && TryExtractRepositoryId(signal.SubjectKey, out var signalRepoId)
                && issueRead.Snapshot.RepositoryId > 0
                && issueRead.Snapshot.RepositoryId != signalRepoId)
            {
                continue;
            }

            var classified = IssueTriageSynapseGenerator.ClassifyIssue(signal.Number, signal.Action, issueRead);

            string receipt;
            if (string.Equals(binding.Mode, IssueTriageSynapseGenerator.AutoCommentMode, StringComparison.Ordinal))
            {
                var requestId = $"synapse-triage:{signal.Number}:{signal.Version}";
                try
                {
                    var commented = await repo.CommentOnIssue(
                        new CommentOnIssueRequest(
                            IssueNumber: signal.Number,
                            Body: classified.MarkdownSummary,
                            Labels: classified.SuggestedLabels,
                            RequestId: requestId),
                        ct).ConfigureAwait(true);
                    receipt = commented.CommentUrl;
                }
                catch (RuleViolationException blocked)
                {
                    StudioLogger.LogInformation(
                        blocked,
                        "Policy guard blocked automatic GitHub triage comment on issue #{IssueNumber} for {ConnectionKey}.",
                        signal.Number,
                        binding.ConnectionKey);
                    var blockedEntry = binding with { LastReceipt = "blocked:github.issue.comment" };
                    await SaveUpdatedBindingAsync(
                        blockedEntry,
                        new IssueTriageBlocked(binding.UnitAlias, signal.Number, version, blocked.Message)).ConfigureAwait(true);
                    continue;
                }
            }
            else
            {
                var preview = await repo.PreviewIssueComment(
                    new PreviewIssueCommentRequest(
                        signal.Number,
                        classified.MarkdownSummary,
                        classified.SuggestedLabels),
                    ct).ConfigureAwait(true);
                receipt = preview.PreviewId;
            }

            var updatedVersions = processed
                .Append(versionKey)
                .TakeLast(MaxProcessedIssueVersions)
                .ToArray();

            var nextEntry = binding with
            {
                ProcessedIssueVersions = updatedVersions,
                TriagedCount = binding.TriagedCount + 1,
                LastIssueNumber = signal.Number,
                LastKind = classified.Kind,
                LastReceipt = receipt,
            };

            await SaveUpdatedBindingAsync(
                nextEntry,
                new IssueTriageRecorded(
                    binding.UnitAlias,
                    signal.Number,
                    version,
                    classified.Kind,
                    binding.Mode,
                    classified.MarkdownSummary,
                    receipt)).ConfigureAwait(true);
        }
    }

    private ILogger StudioLogger =>
        (ILogger?)services.GetService<ILogger<InoStudioNeuron>>() ?? OsConsoleLog.For<InoStudioNeuron>();

    private Task SaveUpdatedBindingAsync(IssueTriageBindingEntry updatedEntry, DigitalBrain.Signal signal)
    {
        var nextBindings = (State.IssueTriageBindings ?? [])
            .Where(b => !string.Equals(b.ConnectionKey, updatedEntry.ConnectionKey, StringComparison.Ordinal))
            .Append(updatedEntry)
            .ToArray();
        return Save(State with { IssueTriageBindings = nextBindings }, signal);
    }

    private async Task Follow<TContract, TSignal>(string connectionKey)
        where TContract : class
        where TSignal : DigitalBrain.Signal
    {
        var neuronType = typeof(TContract).GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type
            ?? throw new InvalidOperationException($"{typeof(TContract).Name} declares no [DefaultNeuronType].");
        var sourceId = new NeuronId(Id.Brain, neuronType, connectionKey);
        if (!Implements<TContract>(sourceId))
        {
            StudioLogger.LogDebug(
                "Skipping Follow<{Contract}, {Signal}>({Key}) because neuron type '{NeuronType}' is not registered in this host.",
                typeof(TContract).Name,
                typeof(TSignal).Name,
                connectionKey,
                neuronType);
            return;
        }

        try
        {
            await Context.Wires.Ensure<TSignal>(sourceId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StudioLogger.LogSwallowed(ex, $"wiring Follow<{typeof(TContract).Name}, {typeof(TSignal).Name}>({connectionKey})");
        }
    }

    private static bool TryExtractRepositoryId(string? subjectKey, out long repositoryId)
    {
        repositoryId = 0;
        if (string.IsNullOrWhiteSpace(subjectKey))
        {
            return false;
        }

        var parts = subjectKey.Split(':');
        return parts.Length >= 2
            && string.Equals(parts[0], "github", StringComparison.Ordinal)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out repositoryId)
            && repositoryId > 0;
    }
}
