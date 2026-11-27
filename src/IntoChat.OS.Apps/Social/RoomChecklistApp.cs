/*
# Room Checklist
Role: Shareable
Shipped: true
Operations: add, toggle, clear-completed

A shareable interactive checklist mini-app backed by server-side CheckBoxNeuron grains for live collaboration inside rooms.
*/
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Host.Apps;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.UI;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Social;

public sealed record ChecklistItem(string Id, string Label, bool Checked, string UpdatedBy = "");

public sealed record RoomChecklistState(
    string Title,
    ChecklistItem[] Items,
    string LastUpdatedBy = "",
    int NextItemNumber = 1,
    string DraftLabel = "")
{
    public RoomChecklistState() : this("Shared Checklist", [], "", 1, "") { }
}

[Signal("room-checklist.edit-draft")]
public sealed record EditChecklistDraft(string Value = "") : Signal;

[Signal("room-checklist.add")]
public sealed record AddChecklistItem(string Label = "") : Signal;

[Signal("room-checklist.toggle")]
public sealed record ToggleChecklistItem(string ItemId = "", bool? Checked = null) : Signal;

[Signal("room-checklist.clear-completed")]
public sealed record ClearCompletedChecklistItems : Signal;

/// <summary>
/// A [Shareable] interactive DigitalBrain.UI mini-app backed by server-side <see cref="CheckBoxNeuron"/> grains.
/// Every participant (and @Ino) interacts with live checkbox neurons with per-caller <c>Caller.For.Account</c> attribution.
/// </summary>
[Shareable]
[Neuron("room.checklist"), Screen("room.checklist", Title = "Shared Checklist", Icon = "check-square", Category = "Social")]
public sealed class RoomChecklistApp : AggregateScreen<RoomChecklistState>
{
    private readonly IServiceProvider? _services;

    public RoomChecklistApp() : this(null) { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public RoomChecklistApp(IServiceProvider? services = null)
    {
        _services = services;
    }

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        await base.OnActivate(cancellationToken);
        foreach (var item in State.Items)
        {
            await FollowChild<ICheckBoxControl, Toggled>(CheckKey(item.Id));
        }
    }

    public static string CheckKey(string itemId) => $"chk-{itemId}";

    private string CallerAccount() => Context.Caller?.For?.Account ?? Id.Brain.Value;

    private IDisposable? OwnModuleIfForeign()
    {
        if (Context.Caller is null || Context.Caller.Brain == Id.Brain)
        {
            return null;
        }

        var edge = _services?.GetService(typeof(EdgeAuthority)) as EdgeAuthority;
        return edge?.Enter(edge.Module(Alias, Id.Brain));
    }

    [On]
    public Task EditDraft(EditChecklistDraft request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Store(State with { DraftLabel = request.Value ?? "" });
    }

    [On]
    public async Task Add(AddChecklistItem request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var label = !string.IsNullOrWhiteSpace(request.Label) ? request.Label.Trim() : State.DraftLabel.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        var nextNumber = Math.Max(State.NextItemNumber, State.Items.Length + 1);
        var id = $"i{nextNumber}";
        var actor = CallerAccount();
        var item = new ChecklistItem(id, label, Checked: false, UpdatedBy: actor);
        using (OwnModuleIfForeign())
        {
            await FollowChild<ICheckBoxControl, Toggled>(CheckKey(id));
        }
        await Show(State with
        {
            Items = [.. State.Items, item],
            LastUpdatedBy = actor,
            NextItemNumber = nextNumber + 1,
            DraftLabel = "",
        });
    }

    [On]
    public Task Toggle(ToggleChecklistItem request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = CallerAccount();
        var updated = State.Items
            .Select(item => string.Equals(item.Id, request.ItemId, StringComparison.Ordinal)
                ? item with { Checked = request.Checked ?? !item.Checked, UpdatedBy = actor }
                : item)
            .ToArray();
        return Show(State with { Items = updated, LastUpdatedBy = actor });
    }

    [On]
    public Task OnChildToggled(Toggled toggled)
    {
        ArgumentNullException.ThrowIfNull(toggled);
        if (SignalChild is not { } childKey || !childKey.StartsWith("chk-", StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        var itemId = childKey["chk-".Length..];
        var actor = CallerAccount();
        var updated = State.Items
            .Select(item => string.Equals(item.Id, itemId, StringComparison.Ordinal)
                ? item with { Checked = toggled.IsOn, UpdatedBy = actor }
                : item)
            .ToArray();
        return Show(State with { Items = updated, LastUpdatedBy = actor });
    }

    [On]
    public async Task ClearCompleted(ClearCompletedChecklistItems request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = CallerAccount();
        var removed = State.Items.Where(static item => item.Checked).ToArray();
        var remaining = State.Items.Where(static item => !item.Checked).ToArray();
        using (OwnModuleIfForeign())
        {
            foreach (var item in removed)
            {
                await UnfollowChild<ICheckBoxControl>(CheckKey(item.Id));
            }
        }
        await Show(State with { Items = remaining, LastUpdatedBy = actor });
    }

    protected override Node Render(RoomChecklistState state)
    {
        var completed = state.Items.Count(static i => i.Checked);
        var subtitle = string.IsNullOrWhiteSpace(state.LastUpdatedBy)
            ? $"{completed}/{state.Items.Length} done"
            : $"{completed}/{state.Items.Length} done · last updated by {state.LastUpdatedBy}";

        var rows = state.Items.Select(item => (Node)HStack(Space.Sm)[
            Child<ICheckBoxControl, CheckBoxProps>(CheckKey(item.Id), new CheckBoxProps(item.Label, IsOn: item.Checked)),
            !string.IsNullOrWhiteSpace(item.UpdatedBy) ? (Node)MutedText($"by {item.UpdatedBy}") : null
        ]).ToArray();

        return Card(state.Title, subtitle)[
            VStack(Space.Sm,
            [
                HStack(Space.Sm)[
                    TextField("New item", "Add checklist item")
                        .Key("input:checklist-item")
                        .Value(state.DraftLabel)
                        .SendsOnChange(new EditChecklistDraft()),
                    PrimaryButton("Add", Icons.Plus)
                        .Key("btn:add-item")
                        .Small()
                        .Sends(Sends(new AddChecklistItem(state.DraftLabel)))
                ],
                .. rows.Length == 0
                    ? [Empty("No items yet", "Add an item to collaborate in this room.")]
                    : rows,
                HStack(Space.Sm)[
                    Button("Clear done", Icons.Trash2).Key("btn:clear-done").Small().Sends(Sends(new ClearCompletedChecklistItems()))
                ],
            ])
        ];
    }
}
