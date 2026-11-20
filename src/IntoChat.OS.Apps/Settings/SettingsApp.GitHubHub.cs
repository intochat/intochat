/*
# Settings
Role: Shell
Shipped: true
Operations: open, read, refresh-connections, save-preferences, reload-preferences, reconcile-preferences, navigate, go-back, export-brain, import-brain, set-variable, set-budget, set-github-triage-policy, install-issue-triage-synapse, ui.command

Declarative macOS Sonoma-style System Settings application for IntoChat OS (`UK-16` / `INT-1364`).
Owns the 13 settings sections, master-detail subpages (`AppPage`, `MemoryPage`, `ConnectionPage`,
`PersonPage`, `LocalServerPage`), modal sheets, search index, navigation backstack, and pure immutable
`SettingsAppState` DDD Aggregate projections (zero transient mutable grain fields!).
*/
using System.Globalization;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Confirmation;
using DigitalBrain.Limits;
using DigitalBrain.Neurons;
using DigitalBrain.Rules;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Apps.Controls;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Visuals;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Approvals;
using IntoChat.OS.Modules.Flutter.Compute;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.Modules.UI.Form.Signals;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Apps.Settings;

public sealed partial class SettingsApp
{
    [On]
    public async Task OnFormSubmitted(FormSubmitted submitted)
    {
        if (submitted is null || string.IsNullOrWhiteSpace(submitted.FormId))
        {
            return;
        }

        if (!string.Equals(submitted.FormId, GitHubPatFormKey, StringComparison.Ordinal)
            && !submitted.FormId.EndsWith("/" + GitHubPatFormKey, StringComparison.Ordinal))
        {
            return;
        }

        string? Value(string field) => submitted.Values is not null
            && submitted.Values.TryGetValue(field, out var raw)
            && !string.IsNullOrWhiteSpace(raw)
                ? raw.Trim()
                : null;

        var rawRepository = Value("Repository");
        if (Decision.Check("connect-github-pat", SettingsAppState.ValidateGitHubRepositoryRule(rawRepository)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var normalizedRepo = SettingsAppState.NormalizeGitHubRepository(rawRepository)!;
        var slug = SettingsAppState.NormalizeGitHubSlug(Value("ConnectionSlug") ?? State.GitHubConnectionSlug);
        var connectionKey = SettingsAppState.NormalizeGitHubConnectionKey(slug);

        var patRef = submitted.SecretRefs is not null && submitted.SecretRefs.TryGetValue("PersonalAccessToken", out var pRef) ? pRef : null;
        var webhookRef = submitted.SecretRefs is not null && submitted.SecretRefs.TryGetValue("WebhookSecret", out var wRef) ? wRef : null;
        var vault = SecretVault ?? TryGetOptional<ISecretVault>("vault");
        string? unsealedPat = null;
        if (patRef is not null && !string.IsNullOrWhiteSpace(patRef.VaultKey) && vault is not null)
        {
            try
            {
                unsealedPat = await vault.TryOpenSecretAsync(patRef.VaultKey);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "Failed opening vault handle {VaultKey} for GitHub PAT.", patRef.VaultKey);
            }
        }

        var grantToken = !string.IsNullOrWhiteSpace(unsealedPat) ? unsealedPat : patRef?.VaultKey;
        var connected = false;
        if (TryBrainOrNull() is { } brain && !string.IsNullOrWhiteSpace(brain.Value) && !string.IsNullOrWhiteSpace(grantToken))
        {
            var connId = new NeuronId(brain, "github", connectionKey);
            try
            {
                var grantView = await Get<IConnectionInbound>(connId).AcceptGrant(new OAuthGrant(
                    AuthMethodId: "pat",
                    AccessToken: grantToken,
                    RefreshToken: null,
                    ExpiresAt: null,
                    Scopes: "issues:write,pull_requests:write,contents:write,metadata:read",
                    Target: normalizedRepo,
                    Response: """{"auth_kind":"pat"}"""));
                connected = grantView.Status == ConnectionStatus.Connected;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IConnectionInbound.AcceptGrant failed on {ConnectionKey}; trying IConnection.Connect.", connectionKey);
                try
                {
                    var connView = await Get<IConnection>(connId).Connect("pat", grantToken);
                    connected = connView.Status == ConnectionStatus.Connected;
                }
                catch (Exception inner) when (inner is not OperationCanceledException)
                {
                    Log.LogDebug(inner, "IConnection.Connect is unavailable in standalone host for {ConnectionKey}.", connectionKey);
                }
            }
        }

        var projected = State.RefreshConnectionsProjection(connectionKey, friendApp: null) with
        {
            Section = "connections",
            SubPage = string.Equals(slug, "main", StringComparison.Ordinal) ? "github" : $"github/{slug}",
            GitHubConnectionSlug = slug,
            GitHubRepository = normalizedRepo,
            GitHubConnected = connected || !string.IsNullOrWhiteSpace(grantToken),
            GitHubWebhookSecretConfigured = State.GitHubWebhookSecretConfigured || webhookRef is not null,
        };

        projected = await RefreshGitHubHubProjectionAsync(projected);
        await UpdateAsync(projected with
        {
            Title = "Settings · connections",
            Status = $"Connected GitHub repository {normalizedRepo} ({connectionKey}).",
        });
    }

    [On]
    public async Task SetGitHubTriagePolicy(SettingsSetGitHubTriagePolicy request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check("set-github-triage-policy", SettingsAppState.ValidateGitHubPolicyPresetRule(request.Preset)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var preset = SettingsAppState.NormalizeGitHubPolicyPreset(request.Preset)!;
        string? pendingPlanId = null;

        if (TryBrainOrNull() is { } brain && !string.IsNullOrWhiteSpace(brain.Value))
        {
            try
            {
                var policies = Get<IPolicies>(NeuronId.Parse(IPolicies.Key(brain.Value)));
                var existing = await policies.List();
                foreach (var rule in existing.Where(static r =>
                    string.Equals(r.Id, GitHubIssueCommentPolicyRuleId, StringComparison.Ordinal)
                    || string.Equals(r.Action, GitHubIssueCommentAction, StringComparison.Ordinal)))
                {
                    await policies.Retire(rule.Id);
                }

                if (string.Equals(preset, "reset", StringComparison.Ordinal))
                {
                    await policies.Retire(GitHubIssueCommentPolicyRuleId);
                }
                else
                {
                    var now = Context.Time.GetUtcNow();
                    var rule = string.Equals(preset, "auto-10", StringComparison.Ordinal)
                        ? new PolicyRule(
                            Id: GitHubIssueCommentPolicyRuleId,
                            Action: GitHubIssueCommentAction,
                            Measure: PolicyMeasure.Count,
                            Window: new PolicyWindow(PolicyWindowKind.Rolling, TimeSpan.FromHours(1)),
                            Limit: 10m,
                            Condition: null,
                            Text: "Allow at most 10 count of comment on a GitHub issue per rolling 01:00:00",
                            AdoptedAt: now)
                        : new PolicyRule(
                            Id: GitHubIssueCommentPolicyRuleId,
                            Action: GitHubIssueCommentAction,
                            Measure: PolicyMeasure.Count,
                            Window: new PolicyWindow(PolicyWindowKind.None, null),
                            Limit: 0m,
                            Condition: null,
                            Text: "Allow at most 0 count of comment on a GitHub issue per call",
                            AdoptedAt: now);

                    var outcome = await policies.Propose(rule);
                    if (outcome.Status == ProposalStatus.Pending && !string.IsNullOrWhiteSpace(outcome.PlanId))
                    {
                        pendingPlanId = outcome.PlanId;
                        try
                        {
                            await Get<IConfirmationInbox>(NeuronId.Parse(IConfirmationInbox.Key(brain.Value)))
                                .Resolve(outcome.PlanId, approve: true);
                            pendingPlanId = null;
                        }
                        catch (Exception confirmEx) when (confirmEx is not OperationCanceledException)
                        {
                            Log.LogDebug(confirmEx, "Policy proposal {PlanId} awaits direct Person confirmation.", outcome.PlanId);
                        }
                    }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IPolicies is optional when SettingsApp runs in standalone tests.");
            }
        }

        var approvals = await TryReadPendingApprovalsAsync();
        var status = preset switch
        {
            "auto-10" => pendingPlanId is null
                ? "Adopted GitHub issue comment policy: up to 10 auto-comments per hour."
                : $"Proposed GitHub issue comment policy (10/hr) · awaiting confirmation ({pendingPlanId}).",
            "draft-only" => pendingPlanId is null
                ? "Adopted GitHub issue comment policy: draft-only (0 auto-posts)."
                : $"Proposed GitHub issue comment policy (draft-only) · awaiting confirmation ({pendingPlanId}).",
            _ => "Reset GitHub issue comment governance policy.",
        };

        await UpdateAsync(State with
        {
            GitHubTriagePolicyPreset = preset,
            GitHubTriagePolicyPlanId = pendingPlanId,
            PendingApprovals = approvals,
            Status = status,
        });
    }

    [On]
    public async Task InstallIssueTriageSynapse(SettingsInstallIssueTriageSynapse request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decision.Check(
                "install-issue-triage-synapse",
                SettingsAppState.ValidateConnectionKey(request.ConnectionKey),
                SettingsAppState.ValidateGitHubSynapseModeRule(request.Mode)) is { } rejected)
        {
            await ApplySettingsDecisionAsync(rejected);
            return;
        }

        var slug = SettingsAppState.NormalizeGitHubSlug(request.ConnectionKey);
        var connectionKey = SettingsAppState.NormalizeGitHubConnectionKey(request.ConnectionKey, State.GitHubConnectionSlug);
        var mode = SettingsAppState.NormalizeGitHubSynapseMode(request.Mode)!;

        InoToolResult result;
        if (TryBrainOrNull() is { } brain && !string.IsNullOrWhiteSpace(brain.Value))
        {
            try
            {
                var studio = Get<IInoStudio>(new NeuronId(brain, "ino.studio", "ino/studio"));
                result = await studio.CreateIssueTriageSynapse(connectionKey, mode);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                Log.LogDebug(error, "IInoStudio is optional when SettingsApp runs in standalone tests.");
                result = new InoToolResult($"Issue triage Synapse installer unavailable: {error.Message}", Ok: false);
            }
        }
        else
        {
            result = new InoToolResult("Brain context is unavailable in detached preview.", Ok: false);
        }

        var deployedUnits = await TryReadDeployedUnitsAsync();
        var alias = result.Ok
            ? ExtractInstalledSynapseAlias(result, deployedUnits, slug)
            : State.GitHubInstalledSynapseAlias;
        var summary = string.IsNullOrWhiteSpace(result.Text)
            ? (result.Ok ? $"Installed {alias} ({mode})." : "Issue triage Synapse installation failed.")
            : result.Text;

        await UpdateAsync(State with
        {
            DeployedUnits = deployedUnits,
            GitHubConnectionSlug = slug,
            GitHubInstalledSynapseAlias = alias,
            GitHubSynapseInstallStatus = summary,
            GitHubSynapseMode = mode,
            Status = summary,
        });
    }

    private async Task<SettingsAppState> RefreshGitHubHubProjectionAsync(SettingsAppState baseline)
    {
        var slug = SettingsAppState.ResolveGitHubSlug(baseline.SubPage, baseline.GitHubConnectionSlug);
        var connectionKey = SettingsAppState.NormalizeGitHubConnectionKey(slug);
        var next = baseline with { GitHubConnectionSlug = slug };

        if (TryBrainOrNull() is not { } brain || string.IsNullOrWhiteSpace(brain.Value))
        {
            return next;
        }

        var connId = new NeuronId(brain, "github", connectionKey);
        ConnectionView? connView = null;
        try
        {
            connView = await Get<IConnection>(connId).Read();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IConnection is optional when probing {ConnectionKey} in standalone tests.", connectionKey);
        }

        try
        {
            var repo = Get<IRepository>(connId);
            var repoView = await repo.Read();
            var isConnected = repoView.Connected || connView?.Status == ConnectionStatus.Connected;
            var repoName = !string.IsNullOrWhiteSpace(connView?.Account)
                ? connView!.Account!.Trim()
                : next.GitHubRepository;
            var prCount = repoView.PullRequests?.Count ?? 0;
            var lastWebhook = repoView.LastWebhookAt?.ToString("u", CultureInfo.InvariantCulture);

            var issueCount = next.GitHubIssueCount;
            var pendingDraftCount = next.GitHubPendingDraftCount;
            if (isConnected)
            {
                try
                {
                    issueCount = (await repo.ReadIssues()).Snapshots.Length;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Log.LogDebug(error, "IRepository.ReadIssues is optional on {ConnectionKey}.", connectionKey);
                }

                try
                {
                    pendingDraftCount = (await repo.ReadPendingIssueDrafts()).Length;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Log.LogDebug(error, "IRepository.ReadPendingIssueDrafts is optional on {ConnectionKey}.", connectionKey);
                }
            }
            else
            {
                issueCount = 0;
                pendingDraftCount = 0;
            }

            next = next with
            {
                GitHubConnected = isConnected,
                GitHubRepository = repoName,
                GitHubLastWebhookAt = lastWebhook,
                GitHubPullRequestCount = prCount,
                GitHubIssueCount = issueCount,
                GitHubPendingDraftCount = pendingDraftCount,
            };

            if (isConnected)
            {
                var keys = (next.ConnectionKeys ?? []).ToList();
                if (!keys.Contains(connectionKey, StringComparer.Ordinal))
                {
                    keys.Add(connectionKey);
                    next = next with { ConnectionKeys = [.. keys] };
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IRepository is optional when probing {ConnectionKey} in standalone tests.", connectionKey);
            if (connView is not null)
            {
                next = next with
                {
                    GitHubConnected = connView.Status == ConnectionStatus.Connected,
                    GitHubRepository = !string.IsNullOrWhiteSpace(connView.Account) ? connView.Account.Trim() : next.GitHubRepository,
                };
            }
        }

        try
        {
            var rules = await Get<IPolicies>(NeuronId.Parse(IPolicies.Key(brain.Value))).List();
            if (rules.FirstOrDefault(static r => string.Equals(r.Action, GitHubIssueCommentAction, StringComparison.Ordinal)) is { } adopted)
            {
                var detectedPreset = adopted.Limit == 0m && adopted.Window.Kind == PolicyWindowKind.None
                    ? "draft-only"
                    : adopted.Limit == 10m && adopted.Window.Kind == PolicyWindowKind.Rolling
                        ? "auto-10"
                        : next.GitHubTriagePolicyPreset;
                next = next with { GitHubTriagePolicyPreset = detectedPreset, GitHubTriagePolicyPlanId = null };
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.LogDebug(error, "IPolicies.List is optional when probing GitHub hub in standalone tests.");
        }

        var deployed = await TryReadDeployedUnitsAsync();
        var detectedSynapse = string.IsNullOrWhiteSpace(next.GitHubInstalledSynapseAlias)
            ? deployed.FirstOrDefault(u => u.StartsWith("gh-triage-", StringComparison.OrdinalIgnoreCase)) ?? string.Empty
            : next.GitHubInstalledSynapseAlias;
        return next with
        {
            DeployedUnits = deployed,
            GitHubInstalledSynapseAlias = detectedSynapse,
        };
    }

    private static string ExtractInstalledSynapseAlias(InoToolResult result, IReadOnlyList<string> deployedUnits, string slug)
    {
        if (result.Card is { } card)
        {
            if (!string.IsNullOrWhiteSpace(card.Id)
                && !card.Id.Contains(':', StringComparison.Ordinal)
                && !card.Id.Contains(' ', StringComparison.Ordinal))
            {
                return card.Id.Trim();
            }

            if (SynapseAliasPattern().Match(card.SourceNeuronUri ?? string.Empty) is { Success: true } uriMatch)
            {
                return uriMatch.Value;
            }
        }

        if (SynapseAliasPattern().Match(result.Text ?? string.Empty) is { Success: true } textMatch)
        {
            return textMatch.Value;
        }

        var expected = $"gh-triage-{slug}";
        if (deployedUnits.Contains(expected, StringComparer.OrdinalIgnoreCase))
        {
            return deployedUnits.First(u => string.Equals(u, expected, StringComparison.OrdinalIgnoreCase));
        }

        return deployedUnits.FirstOrDefault(u => u.StartsWith("gh-triage-", StringComparison.OrdinalIgnoreCase)) ?? expected;
    }
}
