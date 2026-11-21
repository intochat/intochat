using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Confirmation;
using DigitalBrain.Invocation;
using DigitalBrain.Limits;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.UI;
using IntoChat.OS.Modules.UI.Form;
using IntoChat.OS.UI;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Apps.Tests.Unit.Settings;

[Neuron("github")]
public sealed class FakeGitHubHubNeuron : Neuron, IConnection, IConnectionInbound, IRepository
{
    public static OAuthGrant? LastGrant { get; set; }
    public static ConnectionStatus CurrentStatus { get; set; } = ConnectionStatus.Disconnected;
    public static string? CurrentAccount { get; set; }
    public static DateTimeOffset? CurrentLastWebhookAt { get; set; }
    public static PullRequestSnapshot[] CurrentPullRequests { get; set; } = [];
    public static IssueSnapshot[] CurrentIssues { get; set; } = [];
    public static IssueCommentPreview[] CurrentPendingDrafts { get; set; } = [];

    public static void Reset()
    {
        LastGrant = null;
        CurrentStatus = ConnectionStatus.Disconnected;
        CurrentAccount = null;
        CurrentLastWebhookAt = null;
        CurrentPullRequests = [];
        CurrentIssues = [];
        CurrentPendingDrafts = [];
    }

    Task<ConnectionView> IConnection.Read()
        => Task.FromResult(new ConnectionView(
            "github",
            Id.Key.Split('/').Last(),
            "pat",
            CurrentStatus,
            CurrentAccount,
            null,
            CurrentLastWebhookAt,
            null));

    public Task<ConnectionView> Connect(string authMethodId, string value)
    {
        CurrentStatus = ConnectionStatus.Connected;
        return ((IConnection)this).Read();
    }

    public Task<ConnectionView> Probe() => ((IConnection)this).Read();

    public Task Disconnect()
    {
        CurrentStatus = ConnectionStatus.Disconnected;
        return Task.CompletedTask;
    }

    public Task<ConnectionView> Adopt(string authMethodId, string? account) => ((IConnection)this).Read();

    public Task<Dictionary<string, DigitalBrain.Capabilities.Secrets.SealedSecret>> ExportFor(string brainId)
        => Task.FromResult(new Dictionary<string, DigitalBrain.Capabilities.Secrets.SealedSecret>(StringComparer.Ordinal));

    public Task<ConnectionView> Import(
        string authMethodId,
        string? account,
        Dictionary<string, DigitalBrain.Capabilities.Secrets.SealedSecret> secrets,
        ConnectionStatus status,
        string? detail)
        => ((IConnection)this).Read();

    public Task ShareInto(string brainId, string app) => Task.CompletedTask;
    public Task<bool> IsSharedInto(string brainId, string app) => Task.FromResult(false);
    public Task UnshareFrom(string brainId, string app) => Task.CompletedTask;
    public Task ShareWith(string principalId) => Task.CompletedTask;
    public Task<bool> IsSharedWith(string principalId) => Task.FromResult(false);
    public Task UnshareWith(string principalId) => Task.CompletedTask;

    public Task<ConnectionView> AcceptGrant(OAuthGrant grant)
    {
        LastGrant = grant;
        CurrentStatus = ConnectionStatus.Connected;
        CurrentAccount = grant.Target;
        return ((IConnection)this).Read();
    }

    public Task<RepositoryView> Refresh(RefreshRepository request) => ((IRepository)this).Read();

    Task<RepositoryView> IRepository.Read()
        => Task.FromResult(new RepositoryView(
            Id.Key,
            CurrentStatus == ConnectionStatus.Connected,
            CurrentPullRequests,
            CurrentLastWebhookAt));

    public Task<PullRequestRead> ReadPullRequest(ReadPullRequest query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<PullRequestsRead> ReadPullRequests()
        => Task.FromResult(new PullRequestsRead(CurrentPullRequests, CurrentStatus == ConnectionStatus.Connected));

    public Task<ReviewEvidenceRead> ReadReviewEvidence(ReadReviewEvidence query, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RequiredChecksRead> ReadRequiredChecks(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task PushBranch(PushBranchRequest request) => Task.CompletedTask;

    public Task<PullRequestView> CreatePullRequest(CreatePullRequest request) => throw new NotSupportedException();

    public Task ReplyToReviewComment(ReplyReviewComment request) => Task.CompletedTask;

    public Task MergePullRequest(MergePullRequest request) => Task.CompletedTask;

    public Task<IssuesRead> ReadIssues()
        => Task.FromResult(new IssuesRead(CurrentIssues, CurrentStatus == ConnectionStatus.Connected));

    public Task<IssueCommentPreview[]> ReadPendingIssueDrafts()
        => Task.FromResult(CurrentPendingDrafts);
}

[Neuron("ino.studio")]
public sealed class FakeIssueTriageInoStudioNeuron : Neuron, IStudio
{
    public static List<(string ConnectionKey, string Mode)> Calls { get; } = [];

    public static void Reset() => Calls.Clear();

    public Task<InoToolResult> Make(string goal) => Task.FromResult(new InoToolResult(goal));

    public Task<InoToolResult> Launch(string appId) => Task.FromResult(new InoToolResult(appId));

    public Task<InoToolResult> List() => Task.FromResult(new InoToolResult("[]"));

    public Task<InoToolResult> CreateIssueTriageSynapse(string connectionKey, string mode = "DraftForApproval", CancellationToken ct = default)
    {
        Calls.Add((connectionKey, mode));
        var slug = SettingsAppState.NormalizeGitHubSlug(connectionKey);
        var alias = $"gh-triage-{slug}";
        var card = new InoCard(
            Id: alias,
            Kind: "app-install",
            Title: $"GitHub Issue Triage ({slug})",
            Summary: $"Deployed {alias} in {mode} mode.",
            SourceNeuronUri: $"neuron://local/{alias}/default",
            Status: "deployed");
        return Task.FromResult(new InoToolResult($"Deployed issue triage Synapse '{alias}' ({mode}) for {connectionKey}.", card, Ok: true));
    }
}

[CollectionDefinition(nameof(SettingsGitHubHubFacts), DisableParallelization = true)]
public sealed class SettingsGitHubHubCollection;

[Collection(nameof(SettingsGitHubHubFacts))]
public sealed class SettingsGitHubHubFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NavigatedTo_ConnectionsGitHub_OpensGitHubHubDirectlyWithActiveTransitionAndGuardsUnsavedDeparture()
    {
        FakeGitHubHubNeuron.Reset();
        FakeGitHubHubNeuron.CurrentStatus = ConnectionStatus.Connected;
        FakeGitHubHubNeuron.CurrentAccount = "intochat/os";
        FakeGitHubHubNeuron.CurrentLastWebhookAt = new DateTimeOffset(2026, 11, 20, 12, 30, 0, TimeSpan.Zero);
        var observedAt = FakeGitHubHubNeuron.CurrentLastWebhookAt.Value;
        FakeGitHubHubNeuron.CurrentPullRequests =
        [
            new PullRequestSnapshot(
                Number: 42,
                Title: "Add GitHub hub",
                Url: "https://github.com/intochat/os/pull/42",
                IsOpen: true,
                IsDraft: false,
                HeadSha: "1111111111111111111111111111111111111111",
                BaseSha: "2222222222222222222222222222222222222222",
                MergeSha: null,
                CiSha: "1111111111111111111111111111111111111111",
                Checks: [],
                ChecksComplete: true,
                ObservedAt: observedAt,
                CreatedAt: observedAt,
                Revision: "rev-1",
                CiRevision: "ci-1",
                RepositoryId: 12345,
                BaseBranch: "main"),
        ];
        FakeGitHubHubNeuron.CurrentIssues =
        [
            new IssueSnapshot(101, "Triage webhook issue", "Body", "https://github.com/intochat/os/issues/101", true, "ada", ["bug"], 1, observedAt, observedAt, observedAt, "irev-1", 12345),
            new IssueSnapshot(102, "Follow-up feature", "Body", "https://github.com/intochat/os/issues/102", true, "grace", ["enhancement"], 0, observedAt, observedAt, observedAt, "irev-2", 12345),
        ];
        FakeGitHubHubNeuron.CurrentPendingDrafts =
        [
            new IssueCommentPreview("prev-1", 101, "Draft comment", ["triage", "bug"], observedAt.AddMinutes(10), "intochat/os", observedAt),
        ];

        try
        {
            var brain = new BrainId("local");
            using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
            await using var os = TestOS.Local()
                .Register(typeof(AppsModule).Assembly)
                .Register<FakeGitHubHubNeuron>();
            var settings = await os.Install<SettingsApp>();
            var screen = settings.Screen;

            // 1. Plain-input UiNavigateTo ("connections/github") coerces to SettingsNavArgs("connections", "github") and stamps ActiveTransition!
            var navSignal = RuntimeProtocol.SerializeToElement(
                new UiNavigateTo(
                    ArgsJson: null,
                    FromNeuron: "app.engineer",
                    Transition: ViewTransition.SharedAxisX,
                    CorrelationId: "corr-gh-1",
                    Input: "connections/github"),
                typeof(UiNavigateTo));
            await screen.Dispatch(new Command(".", UiNavigateTo.SignalAlias, navSignal));
            await os.Idle();

            Assert.Equal("connections", settings.App.State.Section);
            Assert.Equal("github", settings.App.State.SubPage);
            Assert.Equal(ViewTransition.SharedAxisX, settings.App.ActiveTransition);
            Assert.Equal(ViewTransition.SharedAxisX, settings.App.State.ActiveTransition);
            Assert.True(settings.App.State.GitHubConnected);
            Assert.Equal("intochat/os", settings.App.State.GitHubRepository);
            Assert.Equal(1, settings.App.State.GitHubPullRequestCount);
            Assert.Equal(2, settings.App.State.GitHubIssueCount);
            Assert.Equal(1, settings.App.State.GitHubPendingDraftCount);

            var shell = Assert.IsType<AppShellNode>(settings.App.Render().Root);
            Assert.Equal(ViewTransition.SharedAxisX, shell.Transition);

            var wire = JsonSerializer.Serialize(settings.App.Render(), RuntimeProtocol.OptionsFor(typeof(View)));
            Assert.Contains("Connected", wire, StringComparison.Ordinal);
            Assert.Contains("2026-11-20 12:30:00Z", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github:copy-webhook-url", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github:copy-gh-forward", wire, StringComparison.Ordinal);
            Assert.Contains("/integrations/github/webhook", wire, StringComparison.Ordinal);
            Assert.Contains("gh webhook forward --repo intochat/os --events=issues,issue_comment,pull_request,check_run,check_suite --url=http://localhost:5000/integrations/github/webhook", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github-policy:auto-10", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github-policy:draft-only", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github-policy:reset", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github-synapse:draft", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github-synapse:auto", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github:open-tasks", wire, StringComparison.Ordinal);
            Assert.Contains("settings:github:open-creator", wire, StringComparison.Ordinal);

            // 2. Typed NavigationArrival<SettingsNavArgs> also supports Sheet & SearchQuery
            await settings.App.NavigateToAsync(
                new SettingsNavArgs(Section: "connections", SubPage: "github", Sheet: "add-connection", SearchQuery: "github"),
                fromNeuron: "app.tasks",
                transition: ViewTransition.FadeThrough,
                cancellationToken: Ct);
            Assert.Equal(ViewTransition.FadeThrough, settings.App.ActiveTransition);
            Assert.Equal("add-connection", settings.App.State.Sheet);
            Assert.Equal("github", settings.App.State.SearchQuery);

            // 3. INavigatedFrom: clean state allows departure; HasUnsavedChanges blocks unforced departure, passes when Force == true
            Assert.True(await settings.App.TryNavigateFromAsync(new NavigationDeparture("app.engineer", Force: false), Ct));

            var markDirty = RuntimeProtocol.SerializeToElement(
                new UiCommand(Target: "state", Method: nameof(SettingsAppState.MarkUnsavedChanges), Args: [JsonSerializer.SerializeToElement(true)]),
                typeof(UiCommand));
            await screen.Dispatch(new Command(".", UiCommand.SignalAlias, markDirty));
            await os.Idle();
            Assert.True(settings.App.State.HasUnsavedChanges);

            var unforcedLeave = RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "app.engineer", Route: "flow", Force: false),
                typeof(UiNavigateFrom));
            var refused = await Assert.ThrowsAsync<ActivationRefusedException>(() => screen.Dispatch(new Command(".", UiNavigateFrom.SignalAlias, unforcedLeave)));
            Assert.Contains("settings_unsaved_changes", refused.Reason, StringComparison.Ordinal);

            var forcedLeave = RuntimeProtocol.SerializeToElement(
                new UiNavigateFrom(ToNeuron: "app.engineer", Route: "flow", Force: true),
                typeof(UiNavigateFrom));
            await screen.Dispatch(new Command(".", UiNavigateFrom.SignalAlias, forcedLeave));
            await os.Idle();
            Assert.False(settings.App.State.HasUnsavedChanges);
        }
        finally
        {
            FakeGitHubHubNeuron.Reset();
        }
    }

    [Fact]
    public async Task GitHubPatFormSubmission_ValidatesRepoSealsGrantAndPersistsZeroRawSecrets()
    {
        FakeGitHubHubNeuron.Reset();
        var pat = "github_pat_" + new string('a', 32);
        var webhookSecret = "whsec_" + new string('w', 24);

        try
        {
            var brain = new BrainId("local");
            using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
            await using var os = TestOS.Local()
                .Register(typeof(AppsModule).Assembly)
                .Register(typeof(UiModule).Assembly)
                .Register<FakeGitHubHubNeuron>();

            var settings = await os.Install<SettingsApp>();
            var vault = new MemorySecretVault();
            vault.SetSecret("vault://github/pat/main", pat);
            vault.SetSecret("vault://github/webhook/main", webhookSecret);
            settings.App.SecretVault = vault;

            await settings.App.NavigateToAsync(new SettingsNavArgs("connections", "github"), cancellationToken: Ct);
            await os.Idle();

            var form = os.Get<IForm>(new NeuronId(brain, "ui.form", SettingsApp.GitHubPatFormKey));
            var formState = await form.Read();
            Assert.Equal(4, formState.Fields.Length);
            Assert.Contains(formState.Fields, f => f.Id == "Repository" && f.Kind == FormFieldKinds.Text);
            Assert.Contains(formState.Fields, f => f.Id == "PersonalAccessToken" && f.Kind == FormFieldKinds.Secret);
            Assert.Contains(formState.Fields, f => f.Id == "WebhookSecret" && f.Kind == FormFieldKinds.Secret);
            Assert.Contains(formState.Fields, f => f.Id == "ConnectionSlug" && f.Kind == FormFieldKinds.Text);

            // 1. Invalid repository format is refused by domain rule
            await form.SetValue("Repository", "invalid-repo-without-slash");
            await form.SetSecretRef("PersonalAccessToken", new UiSecretRef("vault://github/pat/main"));
            await form.SetSecretRef("WebhookSecret", new UiSecretRef("vault://github/webhook/main"));
            await form.Submit();
            await os.Idle();

            Assert.NotNull(settings.App.LastRejection);
            Assert.Contains("invalid_github_repository", settings.App.LastRejection!.Rules);
            Assert.Null(FakeGitHubHubNeuron.LastGrant);

            // 2. Valid "owner/repo" seals PAT grant on {brain}/connection/github/main and refreshes live status
            await form.SetValue("Repository", "intochat/os");
            await form.SetValue("ConnectionSlug", "main");
            await form.Submit();
            await os.Idle();

            Assert.Null(settings.App.LastRejection);
            Assert.NotNull(FakeGitHubHubNeuron.LastGrant);
            Assert.Equal("pat", FakeGitHubHubNeuron.LastGrant!.AuthMethodId);
            Assert.Equal("intochat/os", FakeGitHubHubNeuron.LastGrant.Target);
            Assert.Equal(pat, FakeGitHubHubNeuron.LastGrant.AccessToken);

            Assert.True(settings.App.State.GitHubConnected);
            Assert.Equal("intochat/os", settings.App.State.GitHubRepository);
            Assert.True(settings.App.State.GitHubWebhookSecretConfigured);
            Assert.Contains("connection/github/main", settings.App.State.ConnectionKeys!);

            // 3. Prove ZERO raw secrets in SettingsAppState, UiFormState, rendered View, or TestOS journal!
            var formId = new NeuronId(brain, "ui.form", SettingsApp.GitHubPatFormKey);
            var settingsId = new NeuronId(brain, "app.settings", "default");
            var serializedState = JsonSerializer.Serialize(settings.App.State);
            var serializedForm = JsonSerializer.Serialize(await form.Read());
            var serializedView = JsonSerializer.Serialize(settings.App.Render(), RuntimeProtocol.OptionsFor(typeof(View)));
            var journalSignals = os.Journal(brain).Signals(formId)
                .Concat(os.Journal(brain).Signals(settingsId))
                .Select(e => JsonSerializer.Serialize(e.Signal, e.Signal.GetType()));
            var journalDump = string.Join("\n", journalSignals);

            Assert.DoesNotContain(pat, serializedState, StringComparison.Ordinal);
            Assert.DoesNotContain(webhookSecret, serializedState, StringComparison.Ordinal);
            Assert.DoesNotContain(pat, serializedForm, StringComparison.Ordinal);
            Assert.DoesNotContain(webhookSecret, serializedForm, StringComparison.Ordinal);
            Assert.DoesNotContain(pat, serializedView, StringComparison.Ordinal);
            Assert.DoesNotContain(webhookSecret, serializedView, StringComparison.Ordinal);
            Assert.DoesNotContain(pat, journalDump, StringComparison.Ordinal);
            Assert.DoesNotContain(webhookSecret, journalDump, StringComparison.Ordinal);
        }
        finally
        {
            FakeGitHubHubNeuron.Reset();
        }
    }

    [Fact]
    public async Task SetGitHubTriagePolicy_AdoptsAuto10DraftOnlyAndResetRulesOnIPolicies()
    {
        var brain = new BrainId("local");
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
        await using var os = TestOS.Local()
            .Register(typeof(GitHubModule).Assembly)
            .Register<UserSettingsNeuron>();

        var settings = await os.Install<SettingsApp>();
        var screen = settings.Screen;
        var policies = os.Get<IPolicies>(NeuronId.Parse(IPolicies.Key(brain.Value)));
        await settings.App.NavigateToAsync(new SettingsNavArgs("connections", "github"), cancellationToken: Ct);
        await os.Idle();

        // 1. Click settings:github-policy:auto-10 -> 10m per rolling 1-hour window
        var open1 = await screen.Open();
        await screen.Activate("app.settings", open1!.Generation, "settings:github-policy:auto-10");
        await os.Idle();

        Assert.Equal("auto-10", settings.App.State.GitHubTriagePolicyPreset);
        Assert.False(string.IsNullOrWhiteSpace(settings.App.State.GitHubTriagePolicyPlanId));
        await os.Confirmations.Approve(settings.App.State.GitHubTriagePolicyPlanId!);
        await os.Idle();

        var autoRule = Assert.Single(await policies.List(Ct));
        Assert.Equal("github.issue.comment", autoRule.Action);
        Assert.Equal(PolicyMeasure.Count, autoRule.Measure);
        Assert.Equal(PolicyWindowKind.Rolling, autoRule.Window.Kind);
        Assert.Equal(TimeSpan.FromHours(1), autoRule.Window.Span);
        Assert.Equal(10m, autoRule.Limit);

        // 2. Click settings:github-policy:draft-only -> 0m per PolicyWindowKind.None
        var open2 = await screen.Open();
        await screen.Activate("app.settings", open2!.Generation, "settings:github-policy:draft-only");
        await os.Idle();

        Assert.Equal("draft-only", settings.App.State.GitHubTriagePolicyPreset);
        Assert.False(string.IsNullOrWhiteSpace(settings.App.State.GitHubTriagePolicyPlanId));
        await os.Confirmations.Approve(settings.App.State.GitHubTriagePolicyPlanId!);
        await os.Idle();

        var draftRule = Assert.Single(await policies.List(Ct));
        Assert.Equal("github.issue.comment", draftRule.Action);
        Assert.Equal(PolicyWindowKind.None, draftRule.Window.Kind);
        Assert.Equal(0m, draftRule.Limit);
        Assert.False((await policies.Allow("github.issue.comment", 1m, null, Ct)).Allowed);

        // 3. Click settings:github-policy:reset -> retires rule
        var open3 = await screen.Open();
        await screen.Activate("app.settings", open3!.Generation, "settings:github-policy:reset");
        await os.Idle();

        Assert.Equal("reset", settings.App.State.GitHubTriagePolicyPreset);
        Assert.Empty(await policies.List(Ct));
        Assert.True((await policies.Allow("github.issue.comment", 1m, null, Ct)).Allowed);
    }

    [Fact]
    public async Task InstallIssueTriageSynapse_InvokesInoStudioAndUpdatesCreatorAndTasksDeepLinks()
    {
        FakeIssueTriageInoStudioNeuron.Reset();
        try
        {
            var brain = new BrainId("local");
            using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", brain));
            await using var os = TestOS.Local()
                .Register<UserSettingsNeuron>()
                .Register<FakeIssueTriageInoStudioNeuron>();

            var settings = await os.Install<SettingsApp>();
            var screen = settings.Screen;
            await settings.App.NavigateToAsync(new SettingsNavArgs("connections", "github"), cancellationToken: Ct);
            await os.Idle();

            // 1. Install Draft mode via button activation
            var openDraft = await screen.Open();
            await screen.Activate("app.settings", openDraft!.Generation, "settings:github-synapse:draft");
            await os.Idle();

            Assert.Single(FakeIssueTriageInoStudioNeuron.Calls);
            Assert.Equal(("connection/github/main", "DraftForApproval"), FakeIssueTriageInoStudioNeuron.Calls[0]);
            Assert.Equal("gh-triage-main", settings.App.State.GitHubInstalledSynapseAlias);
            Assert.Equal("DraftForApproval", settings.App.State.GitHubSynapseMode);

            var wireAfterDraft = JsonSerializer.Serialize(settings.App.Render(), RuntimeProtocol.OptionsFor(typeof(View)));
            Assert.Contains("gh-triage-main", wireAfterDraft, StringComparison.Ordinal);
            Assert.Contains("os://window/open?app=tasks\\u0026input=approvals", wireAfterDraft, StringComparison.Ordinal);
            Assert.Contains("os://window/open?app=creator\\u0026input=gh-triage-main", wireAfterDraft, StringComparison.Ordinal);

            // 2. Install AutoComment mode via button activation
            var openAuto = await screen.Open();
            await screen.Activate("app.settings", openAuto!.Generation, "settings:github-synapse:auto");
            await os.Idle();

            Assert.Equal(2, FakeIssueTriageInoStudioNeuron.Calls.Count);
            Assert.Equal(("connection/github/main", "AutoComment"), FakeIssueTriageInoStudioNeuron.Calls[1]);
            Assert.Equal("AutoComment", settings.App.State.GitHubSynapseMode);
        }
        finally
        {
            FakeIssueTriageInoStudioNeuron.Reset();
        }
    }

    [Fact]
    public void GitHubHubStates_PassAggregateExplorerPreviewWithZeroViolationsAndZeroMonospace()
    {
        var hubState = new SettingsAppState()
            .ApplyArrival(new SettingsNavArgs("connections", "github"), ViewTransition.FadeThrough) with
        {
            GitHubConnected = true,
            GitHubRepository = "intochat/os",
            GitHubLastWebhookAt = "2026-11-20 12:00:00Z",
            GitHubPullRequestCount = 3,
            GitHubIssueCount = 5,
            GitHubPendingDraftCount = 2,
            GitHubWebhookSecretConfigured = true,
            GitHubTriagePolicyPreset = "draft-only",
            GitHubInstalledSynapseAlias = "gh-triage-main",
            GitHubSynapseInstallStatus = "Deployed gh-triage-main (DraftForApproval).",
            GitHubSynapseMode = "DraftForApproval",
        };

        Assert.True(hubState.ValidGitHubHubProjectionInvariant);
        var node = AggregateExplorer<SettingsAppState>.PreviewNode<SettingsApp>(hubState);
        var wire = JsonSerializer.Serialize(node, RuntimeProtocol.OptionsFor(typeof(Node)));
        Assert.DoesNotContain("\"mono\":true", wire, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GitHub Hub \\u00B7 main", wire, StringComparison.Ordinal);
    }
}
