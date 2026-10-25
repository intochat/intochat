using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Storage;
using DigitalBrain.Confirmation;
using DigitalBrain.Contracts;
using DigitalBrain.Operations;
using DigitalBrain.UI;
using DigitalBrain.UI.Confirmation;
using OS = DigitalBrain.Testing.TestOS;

namespace DigitalBrain.Testing.Tests.Unit.Confirmation;

[NeuronAlias("test.confirmer")]
public interface IConfirmer
{
    Task<string> Delete(string item);

    Task<string> Ask(string planId, bool destructive);

    Task Relay(string planId);
}

/// <summary>Asks through Context.Confirm and Context.Operations, and tries to relay an approval as itself.</summary>
[Neuron("test.confirmer")]
public sealed class ConfirmerNeuron : Neuron, IConfirmer
{
    public async Task<string> Delete(string item)
    {
        var operation = (FakeOperation)await Context.Operations.Start(WritePlan.Delete(new ItemId(item)), CancellationToken.None);
        return await operation.Completion switch
        {
            OperationResult.Failed failed => "failed:" + failed.Code,
            var other => other.GetType().Name.ToLowerInvariant(),
        };
    }

    public async Task<string> Ask(string planId, bool destructive)
    {
        var result = await Context.Confirm(ConfirmationFacts.Plan(planId, destructive), CancellationToken.None);
        return $"{result.Outcome}:{result.Reason}:{result.Skipped}";
    }

    public Task Relay(string planId) => Context.Confirmations.Resolve(planId, approve: true, CancellationToken.None);
}

// 07-02 (CAP-06, FILES-04, D-03, D-09, D-16, D-19, D-22): the confirmation gate contract, its TestOS implementation and ConfirmView.
public sealed class ConfirmationFacts
{
    private static readonly ConfirmationPrincipal App = ConfirmationPrincipal.App("ada/files/app");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static ConfirmationPlan Plan(string id, bool destructive)
        => new(id, destructive ? ConfirmationAction.Delete : ConfirmationAction.Rename, destructive, "Delete a.txt?", "This removes a.txt from Drive. You cannot undo this.", "Delete", "Keep", "a.txt");

    private static async Task<ConfirmationRequested> Waiting(TestConfirmations confirmations, string id)
    {
        for (var i = 0; i < 500; i++)
        {
            if (confirmations.Waiting.FirstOrDefault(pending => pending.PlanId == id) is { } found) { return found; }
            await Task.Delay(10, Ct);
        }
        throw new TimeoutException($"Plan {id} was never asked.");
    }

    [Fact]
    public void The_policy_has_exactly_always_destructive_only_never_and_defaults_to_destructive_only()
    {
        Assert.Equal(["DestructiveOnly", "Always", "Never"], Enum.GetNames<ConfirmationPolicy>());
        Assert.Equal(ConfirmationPolicy.DestructiveOnly, default);
        Assert.Equal(ConfirmationPolicy.DestructiveOnly, new TestConfirmations(new TestClock()).Policy);
        Assert.Equal(TimeSpan.FromMinutes(5), Plan("p", true).Expiry);
    }

    [Fact]
    public async Task Destructive_only_approves_a_non_destructive_plan_without_asking_and_audits_it_skipped()
    {
        var confirmations = new TestConfirmations(new TestClock());
        var published = new List<Signal>();

        var result = await confirmations.For(App, signal => { published.Add(signal); return Task.CompletedTask; }).Confirm(Plan("p1", false), Ct);

        Assert.Equal(new ConfirmationResult("p1", ConfirmationOutcome.Approved, ConfirmationDenyReason.None, Skipped: true), result);
        Assert.Empty(confirmations.Requested);
        var audit = Assert.IsType<ConfirmationResolved>(Assert.Single(published));
        Assert.True(audit.Skipped);
        Assert.Equal(App, audit.Requester);
        Assert.Single(confirmations.Audit);
    }

    [Fact]
    public async Task Never_approves_even_a_destructive_plan_and_still_writes_the_audit_signal()
    {
        var confirmations = new TestConfirmations(new TestClock()) { Policy = ConfirmationPolicy.Never };

        var result = await confirmations.For(App).Confirm(Plan("p2", true), Ct);

        Assert.True(result.Approved);
        Assert.True(result.Skipped);
        var audit = Assert.Single(confirmations.Audit);
        Assert.Equal((ConfirmationOutcome.Approved, true, ConfirmationPolicy.Never), (audit.Outcome, audit.Skipped, audit.Policy));
    }

    [Fact]
    public async Task Always_asks_even_a_non_destructive_plan_and_waits_for_a_person()
    {
        var confirmations = new TestConfirmations(new TestClock()) { Policy = ConfirmationPolicy.Always };
        var published = new List<Signal>();

        var asked = confirmations.For(App, signal => { published.Add(signal); return Task.CompletedTask; }).Confirm(Plan("p3", false), Ct);
        var request = await Waiting(confirmations, "p3");
        Assert.False(asked.IsCompleted);
        Assert.Equal((App, ConfirmationPolicy.Always), (request.Requester, request.Policy));

        await confirmations.Approve("p3");

        var result = await asked;
        Assert.Equal((ConfirmationOutcome.Approved, false), (result.Outcome, result.Skipped));
        Assert.IsType<ConfirmationRequested>(published[0]);
        var resolved = Assert.IsType<ConfirmationResolved>(published[1]);
        Assert.Equal(("p3", ConfirmationPrincipal.Person("person")), (resolved.PlanId, resolved.Approver));
    }

    [Fact]
    public async Task An_app_or_agent_approving_is_denied_self_approval()
    {
        var confirmations = new TestConfirmations(new TestClock());
        var asked = confirmations.For(App).Confirm(Plan("p4", true), Ct);
        await Waiting(confirmations, "p4");

        confirmations.Current = ConfirmationPrincipal.Agent("agent-7");
        await confirmations.Approve("p4");

        var result = await asked;
        Assert.Equal((ConfirmationOutcome.Denied, ConfirmationDenyReason.SelfApproval), (result.Outcome, result.Reason));
        Assert.Equal(PrincipalKind.Agent, Assert.Single(confirmations.Audit).Approver!.Kind);
    }

    [Fact]
    public async Task No_answer_within_five_minutes_expires_on_the_clock()
    {
        var clock = new TestClock();
        var confirmations = new TestConfirmations(clock);
        var asked = confirmations.For(App).Confirm(Plan("p5", true), Ct);
        var request = await Waiting(confirmations, "p5");
        Assert.Equal(clock.GetUtcNow() + TimeSpan.FromMinutes(5), request.ExpiresAt);

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.False(asked.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = await asked;
        Assert.Equal((ConfirmationOutcome.Expired, ConfirmationDenyReason.Expired), (result.Outcome, result.Reason));
        Assert.Null(Assert.Single(confirmations.Audit).Approver);
        Assert.Empty(confirmations.Waiting);
    }

    [Fact]
    public async Task The_first_answer_wins_and_a_second_answer_writes_nothing()
    {
        var confirmations = new TestConfirmations(new TestClock());
        var asked = confirmations.For(App).Confirm(Plan("p6", true), Ct);
        await Waiting(confirmations, "p6");

        await confirmations.Deny("p6");
        await confirmations.Approve("p6");

        Assert.Equal((ConfirmationOutcome.Denied, ConfirmationDenyReason.ByPerson), ((await asked).Outcome, (await asked).Reason));
        Assert.Single(confirmations.Audit);
    }

    // D-19: the buttons target the runtime confirmations neuron, never the app.
    [Fact]
    public void Confirm_view_is_a_card_with_the_static_expiry_line_and_two_buttons_bound_to_the_confirmations_neuron()
    {
        var card = ConfirmView.For("p7", "Delete a.txt?", "This removes a.txt from Drive. You cannot undo this.", "Delete", "Keep", TimeSpan.FromMinutes(5), viewerIsHuman: true);

        Assert.Equal(("Delete a.txt?", "This removes a.txt from Drive. You cannot undo this."), (card.Title, card.Body));
        Assert.Equal("This request expires in 5 minutes.", Assert.IsType<Text>(card.Children[0]).Value);
        var buttons = card.Children.OfType<Button>().ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Equal(("Delete", ButtonTone.Accent, true), (buttons[0].Label, buttons[0].Tone, buttons[0].Enabled));
        Assert.Equal(("Keep", ButtonTone.Neutral), (buttons[1].Label, buttons[1].Tone));
        Assert.Equal(new ButtonAction("confirmations", "Resolve", ["p7", "true"]), buttons[0].Action);
        Assert.Equal(new ButtonAction("confirmations", "Resolve", ["p7", "false"]), buttons[1].Action);
        Assert.Equal(IConfirmations.ConfirmationsNeuronKey, ConfirmView.ConfirmationsNeuronKey);
        Assert.Equal(IConfirmations.ResolveOperation, ConfirmView.ResolveOperation);
    }

    [Fact]
    public void Confirm_view_for_an_app_or_agent_viewer_disables_approve_and_says_it_waits()
    {
        var card = ConfirmView.For("p8", "t", "b", "Delete", "Keep", TimeSpan.FromMinutes(5), viewerIsHuman: false);

        Assert.Contains(card.Children, node => node is Text { Value: "Waiting for you to approve." });
        var buttons = card.Children.OfType<Button>().ToList();
        Assert.False(buttons[0].Enabled);
        Assert.True(buttons[1].Enabled);
    }

    [Fact]
    public async Task A_person_clicking_the_rendered_approve_button_approves()
    {
        await using var os = OS.Local();
        os.Register<ConfirmerNeuron>();
        var confirmer = os.Get<IConfirmer>(new NeuronId(new BrainId("ada"), "test.confirmer", "c"));

        var asked = confirmer.Ask("p9", destructive: true);
        var request = await Waiting(os.Confirmations, "p9");
        Assert.Equal(PrincipalKind.App, request.Requester.Kind);
        var card = ConfirmView.For(request.PlanId, request.Plan.Title, request.Plan.Body, request.Plan.ApproveLabel, request.Plan.DenyLabel, request.Plan.Expiry, viewerIsHuman: true);

        os.Confirmations.Current = ConfirmationPrincipal.Person("ada");
        await os.Confirmations.Click(card.Children.OfType<Button>().First());

        Assert.Equal("Approved:None:False", await asked);
        Assert.Equal(ConfirmationPrincipal.Person("ada"), Assert.Single(os.Confirmations.Audit).Approver);
        var journal = await os.Journal(new BrainId("ada")).Read(new NeuronId(new BrainId("ada"), "test.confirmer", "c"), 0, SignalPage.MaxLimit, Ct);
        Assert.Contains(journal.Signals, item => item.Signal is ConfirmationRequested);
        Assert.Contains(journal.Signals, item => item.Signal is ConfirmationResolved);
    }

    [Fact]
    public async Task An_app_relaying_resolve_itself_is_denied_self_approval()
    {
        await using var os = OS.Local(options => options.CallTimeout = TimeSpan.FromHours(1));
        os.Register<ConfirmerNeuron>();
        var asker = os.Get<IConfirmer>(new NeuronId(new BrainId("ada"), "test.confirmer", "asker"));
        var relay = os.Get<IConfirmer>(new NeuronId(new BrainId("ada"), "test.confirmer", "relay"));

        var asked = asker.Ask("p10", destructive: true);
        await Waiting(os.Confirmations, "p10");
        await relay.Relay("p10");

        Assert.Equal("Denied:SelfApproval:False", await asked);
        Assert.Equal(PrincipalKind.App, Assert.Single(os.Confirmations.Audit).Approver!.Kind);
    }

    public static TheoryData<ConfirmationPolicy, string, bool> Matrix()
    {
        var data = new TheoryData<ConfirmationPolicy, string, bool>();
        var destructive = new Dictionary<string, bool>
        {
            ["delete"] = true,
            ["cross-move"] = true,
            ["overwrite"] = true,
            ["create-folder"] = false,
            ["rename"] = false,
            ["same-copy"] = false,
        };
        foreach (var (op, isDestructive) in destructive)
        {
            data.Add(ConfirmationPolicy.Always, op, true);
            data.Add(ConfirmationPolicy.DestructiveOnly, op, isDestructive);
            data.Add(ConfirmationPolicy.Never, op, false);
        }
        return data;
    }

    // D-22 policy matrix through the storage operation gate (the same StorageConfirmationGate the runtime's default gate uses).
    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task The_storage_gate_asks_by_policy(ConfirmationPolicy policy, string op, bool asks)
    {
        var a = new FakeStorage("Drive");
        var b = new FakeStorage("Disk");
        var file = a.AddFile(a.Root, "a.txt", "x"u8.ToArray());
        var connections = new[] { new CapabilityConnection<IStorage>("drive", "Drive", a), new CapabilityConnection<IStorage>("disk", "Disk", b) };
        var runner = new StorageWriteRunner(async (item, ct) =>
        {
            foreach (var connection in connections)
            {
                try { _ = await connection.Capability.Stat(item, ct); return connection; }
                catch (StorageException) { }
            }
            throw new NotFound(item);
        }, (_, _, _) => Task.FromResult<OperationResult>(new OperationResult.Succeeded()));
        var plan = op switch
        {
            "delete" => WritePlan.Delete(file),
            "cross-move" => WritePlan.Move(file, b.Root),
            "overwrite" => WritePlan.WriteFile(a.Root, "a.txt", "y"u8.ToArray(), ConflictPolicy.Overwrite),
            "create-folder" => WritePlan.CreateFolder(a.Root, "New"),
            "rename" => WritePlan.Rename(file, "b.txt"),
            _ => WritePlan.Copy(file, a.Root),
        };
        var confirmations = new TestConfirmations(new TestClock()) { Policy = policy };

        var check = StorageConfirmationGate.Check(runner, confirmations.For(App), plan, Ct);
        if (asks)
        {
            var request = await WaitAny(confirmations);
            Assert.DoesNotContain("/", request.Plan.Title + request.Plan.Body, StringComparison.Ordinal);
            await confirmations.Deny(request.PlanId);
            Assert.Equal("The confirmation was denied.", await check);
        }
        else
        {
            Assert.Null(await check);
            Assert.Empty(confirmations.Requested);
        }
        var audit = Assert.Single(confirmations.Audit);
        Assert.Equal(!asks, audit.Skipped);
    }

    private static async Task<ConfirmationRequested> WaitAny(TestConfirmations confirmations)
    {
        for (var i = 0; i < 500; i++)
        {
            if (confirmations.Waiting.Count > 0) { return confirmations.Waiting[0]; }
            await Task.Delay(10, Ct);
        }
        throw new TimeoutException("Nothing was asked.");
    }

    [Fact]
    public void Delete_copy_names_item_and_storage_and_offers_delete_and_keep()
    {
        var plan = StorageConfirmation.For("p", WritePlan.Delete(new ItemId("x")), false, "report.pdf", "Drive", "Drive", "Root");

        Assert.Equal(("Delete report.pdf?", "This removes report.pdf from Drive. You cannot undo this.", "Delete", "Keep"), (plan.Title, plan.Body, plan.ApproveLabel, plan.DenyLabel));
        Assert.True(plan.Destructive);
    }

    [Fact]
    public async Task A_delete_through_context_operations_waits_for_a_person_and_is_refused_when_denied()
    {
        await using var os = OS.Local(options => options.CallTimeout = TimeSpan.FromHours(1));
        var drive = new FakeStorage("Drive");
        var file = drive.AddFile(drive.Root, "a.txt", "x"u8.ToArray());
        os.WithStorage("drive", drive);
        os.Register<ConfirmerNeuron>();
        var confirmer = os.Get<IConfirmer>(new NeuronId(new BrainId("ada"), "test.confirmer", "d"));

        var deleting = confirmer.Delete(file.Value);
        var request = await WaitAny(os.Confirmations);
        Assert.Equal(("Delete a.txt?", ConfirmationAction.Delete), (request.Plan.Title, request.Plan.Action));
        await os.Confirmations.Deny(request.PlanId);

        Assert.Equal("failed:denied", await deleting);
        Assert.Contains((await drive.List(drive.Root, new ListQuery(), Ct)).Items, item => item.Name == "a.txt");
    }
}
