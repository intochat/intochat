/*
# UI Kit
Role: Default
Shipped: true
Operations: open, append, showcaseCommand

Working, clickable prototypes of every control: one page per catalog kind and a page per chat scenario. Control neuron kinds are real
slotted children (slot key = the kind); data node kinds are drawn inline from their [Showcase] examples. The app follows every child's
domain signals and keeps the last 50 in an event log shown on every page: the visible proof each control signals. Controls are
uncontrolled (they keep their own state) except the switch labelled as controlled, whose value the app owns.
*/
using IntoChat.OS.Host.Apps;
using System.Globalization;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using IntoChat.OS.Modules.UI.Controls;
using IntoChat.OS.UI;

namespace IntoChat.OS.Apps.UiKit;

[Neuron("app.uikit")]
[Screen("app.uikit", Title = "UI Kit", Icon = "widgets", Category = "Developer")]
public sealed class UiKitApp : AggregateScreen<UiKitState>
{
    public const string AppName = "uikit";

    private string _brain = "preview";

    public static AppBundle Bundle => AppBundle.FromScreen<UiKitApp>(
        AppName,
        "1.0.0",
        description: "Working prototypes of every control, with a log of the signals they send.",
        operations:
        [
            new("open", "Show the page the input names: a scenario id or a catalog kind (the first scenario when empty or unknown)."),
            new("append", "Append the next sample point to the live chart the current page shows."),
            new("showcaseCommand", "Log a command a data node example sent (its kind, signal and args)."),
        ]);

    public new UiKitState State => base.State;

    protected override string Title => "UI Kit";

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        _brain = Id.Brain.Value;
        await base.OnActivate(cancellationToken);
        await FollowChild<IListBoxControl, SelectionChanged>(UiKitPages.Nav);
        await FollowChild<ICheckBoxControl, Toggled>("checkBox");
        await FollowChild<IToggleSwitchControl, Toggled>("toggleSwitch");
        await FollowChild<IToggleSwitchControl, Toggled>(UiKitPages.ControlledSwitch);
        await FollowChild<IExpanderControl, Expanded>("expander");
        await FollowChild<ITabStripControl, SelectionChanged>("tabStrip");
        await FollowChild<IComboBoxControl, SelectionChanged>("comboBox");
        await FollowChild<IComboBoxControl, SelectionChanged>(UiKitPages.ModelPicker);
        await FollowChild<IListBoxControl, SelectionChanged>("listBox");
        await FollowChild<IFormControl, Submitted>("form");
        await FollowChild<ILiveChartControl, ChartChanged>("liveChart");
        await FollowChild<ILiveChartControl, ChartChanged>(UiKitPages.LiveChart);
        await FollowChild<IQueryTableControl, TableChanged>("queryTable");
        await FollowChild<IQueryTableControl, TableChanged>(UiKitPages.TableQuery);
        foreach (var thread in ThreadSlots)
        {
            await FollowChild<IChatThreadControl, MessageAppended>(thread);
            await FollowChild<IChatThreadControl, MessageUpdated>(thread);
            await FollowChild<IChatThreadControl, RetryRequested>(thread);
            await FollowChild<IChatThreadControl, Submitted>(thread);
            await FollowChild<IChatThreadControl, StopRequested>(thread);
        }
    }

    private static readonly string[] ThreadSlots =
        ["chatThread", UiKitPages.Message, UiKitPages.FailedReply, UiKitPages.CardInChat, UiKitPages.ActivityRow, UiKitPages.Composer, UiKitPages.Empty, UiKitPages.Waiting];

    [On]
    public Task Open(UiKitOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ShowPage(UiKitPages.Find(request.Page)?.Id ?? UiKitPages.First, State);
    }

    [On]
    public async Task Append(UiKitAppend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chart = State.Page switch { "liveChart" => "liveChart", UiKitPages.LiveChart => UiKitPages.LiveChart, _ => null };
        if (chart is null) { return; }
        var number = State.Appended + 1;
        await Show(State with { Appended = number });
        await SendTo(chart, new Append(UiKitPages.Point(number)));
    }

    [On]
    public Task OnSelectionChanged(SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var logged = Log(changed);
        return SignalChild switch
        {
            UiKitPages.Nav when UiKitPages.Find(changed.Id) is { } page => ShowPage(page.Id, logged),
            UiKitPages.ModelPicker when UiKitPages.Models.Any(model => model.Id == changed.Id) => Show(logged with { Model = changed.Id }),
            _ => Show(logged),
        };
    }

    [On]
    public Task OnToggled(Toggled toggled)
    {
        ArgumentNullException.ThrowIfNull(toggled);
        var logged = Log(toggled);
        return Show(SignalChild == UiKitPages.ControlledSwitch ? logged with { ControlledOn = toggled.IsOn } : logged);
    }

    [On]
    public Task ShowcaseCommand(UiKitShowcaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Show(State.Logged(new UiKitEvent(command.Kind ?? "(example)", command.OriginalSignal ?? "", command.ArgsJson ?? "null", Context.Time.GetUtcNow())));
    }

    [On] public Task OnExpanded(Expanded expanded) => Show(Log(expanded));
    [On] public Task OnSubmitted(Submitted submitted) => Show(Log(submitted));
    [On] public Task OnChartChanged(ChartChanged changed) => Show(Log(changed));
    [On] public Task OnTableChanged(TableChanged changed) => Show(Log(changed));
    [On] public Task OnMessageAppended(MessageAppended appended) => Show(Log(appended));
    [On] public Task OnMessageUpdated(MessageUpdated updated) => Show(Log(updated));
    [On] public Task OnRetryRequested(RetryRequested requested) => Show(Log(requested));
    [On] public Task OnStopRequested(StopRequested requested) => Show(Log(requested));

    private UiKitState Log<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        return State.Logged(new UiKitEvent(SignalChild ?? "(outside)", AliasOf(typeof(TSignal)),
            JsonSerializer.Serialize(signal, typeof(TSignal), JsonSerializerOptions.Web), Context.Time.GetUtcNow()));
    }

    // Shows a page; a scenario page then writes its sample messages into its chat thread (idempotent by id, so every show is safe).
    private async Task ShowPage(string page, UiKitState basis)
    {
        await Show(basis with { Page = page });
        if (UiKitPages.Seed(page) is { Count: > 0 } seed) { await SendTo(page, new ImportMessages(seed)); }
        // A chart starts with a few points: the chart ignores an event id it holds, so a later show (or one after Append) adds nothing.
        if (page is "liveChart" or UiKitPages.LiveChart)
        {
            foreach (var point in UiKitPages.ChartSeed) { await SendTo(page, new Append(point)); }
        }
    }

    private static string AliasOf(Type signal) => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<SignalAttribute>(signal)!.Alias;

    private Task SendTo<TCommand>(string slot, TCommand command) where TCommand : Signal
        => Send(new Command(slot, AliasOf(typeof(TCommand)), RuntimeProtocol.SerializeToElement(command, typeof(TCommand))));

    protected override Node Render(UiKitState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var page = UiKitPages.Find(state.Page) ?? UiKitPages.All[0];
        var nav = Child<IListBoxControl, ListBoxProps>(UiKitPages.Nav, new ListBoxProps(
            [.. UiKitPages.All.Select(item => new ListBoxItem(item.Id, item.Title, Group: item.Group))], AutomationName: "UI Kit pages", SelectedId: page.Id));
        return new SplitPane(nav, new Column(
        [
            new Heading(page.Title, 1, page.Group),
            .. Page(page, state),
            new Divider("Event log"),
            .. EventLog(state),
        ]));
    }

    private IEnumerable<Node> Page(UiKitPage page, UiKitState state)
    {
        if (page.Group == UiKitPages.DataNode)
        {
            var examples = UiKitPages.Examples(page.Id, UiKitPages.NodeKinds[page.Id]);
            return examples.Count > 0 ? examples : [new Text($"The {page.Id} node has no [Showcase] example yet.")];
        }
        return page.Id switch
        {
            "checkBox" => [Child<ICheckBoxControl, CheckBoxProps>("checkBox", new CheckBoxProps("Remember me"))],
            "toggleSwitch" =>
            [
                Child<IToggleSwitchControl, ToggleSwitchProps>("toggleSwitch", new ToggleSwitchProps("Wi-Fi (uncontrolled: the switch keeps its value)", "On", "Off")),
                Child<IToggleSwitchControl, ToggleSwitchProps>(UiKitPages.ControlledSwitch,
                    new ToggleSwitchProps("Controlled: the UI Kit owns this value", "On", "Off", IsOn: state.ControlledOn)),
                new Text(state.ControlledOn ? "The UI Kit stored: on" : "The UI Kit stored: off"),
            ],
            "expander" => [Child<IExpanderControl, ExpanderProps>("expander", new ExpanderProps("Details", [new Text("Shown while expanded.")]))],
            "tabStrip" => [Child<ITabStripControl, TabStripProps>("tabStrip", new TabStripProps([new("home", "Home"), new("files", "Files"), new("share", "Share")]))],
            "comboBox" => [Child<IComboBoxControl, ComboBoxProps>("comboBox", new ComboBoxProps("Theme", [new("light", "Light"), new("dark", "Dark"), new("auto", "Auto")]))],
            "listBox" => [Child<IListBoxControl, ListBoxProps>("listBox", new ListBoxProps([new("ana", "Ana", "Design"), new("ben", "Ben", "Research"), new("cy", "Cy")]))],
            "form" => [Child<IFormControl, FormProps>("form", new FormProps(
                [new FormInput("name", "Name", Placeholder: "Your name", MaxLength: 40), new FormInput("age", "Age", FormFieldKind.Number), new FormInput("bio", "Bio", FormFieldKind.Multiline)],
                "Save"))],
            "liveChart" or UiKitPages.LiveChart => [Chart(page.Id)],
            "queryTable" or UiKitPages.TableQuery => [Child<IQueryTableControl, QueryTableProps>(page.Id, new QueryTableProps("People",
                [new("id", "Id"), new("name", "Name"), new("status", "Status")], $"{_brain}/control.sampleTableSource/{UiKitPages.SampleSourceKey}"))],
            "chatThread" => [Thread("chatThread", new ChatThreadProps("Write a message", AutomationName: "Chat thread"))],
            UiKitPages.Message => [Thread(page.Id, new ChatThreadProps(AutomationName: "Message"))],
            UiKitPages.FailedReply => [Thread(page.Id, new ChatThreadProps(Status: "failed", AutomationName: "Failed reply"))],
            UiKitPages.CardInChat => [Thread(page.Id, new ChatThreadProps(AutomationName: "Card in chat"))],
            UiKitPages.ActivityRow => [Thread(page.Id, new ChatThreadProps(AutomationName: "Activity row"))],
            UiKitPages.Composer =>
            [
                Child<IComboBoxControl, ComboBoxProps>(UiKitPages.ModelPicker, new ComboBoxProps("Model", UiKitPages.Models)),
                Thread(page.Id, new ChatThreadProps("Message the model", ActiveModelLabel: UiKitPages.Models.First(model => model.Id == state.Model).Label, AutomationName: "Composer")),
            ],
            UiKitPages.Empty => [Thread(page.Id, new ChatThreadProps(EmptyText: "Ask anything to start.", AutomationName: "Empty"))],
            UiKitPages.Waiting => [Thread(page.Id, new ChatThreadProps(CanSend: false, Status: "working", Working: true, AutomationName: "Waiting"))],
            _ => [new Text($"The {page.Id} control has no page yet.")],
        };
    }

    private Node Thread(string key, ChatThreadProps props) => Child<IChatThreadControl, ChatThreadProps>(key, props);

    private Node Chart(string key) => new Column(
    [
        Child<ILiveChartControl, LiveChartProps>(key, new LiveChartProps("Live points")),
        new Button("append", "Append point", Sends(new UiKitAppend()), Tone: ButtonTone.Accent, AutomationName: "Append point"),
    ]);

    private static IEnumerable<Node> EventLog(UiKitState state)
        => state.Log.Length == 0
            ? [new Text("No signals yet: use a control.")]
            : state.Log.Reverse().Select(entry => (Node)new Text(string.Create(CultureInfo.InvariantCulture,
                $"{entry.At:HH:mm:ss} {entry.Control} {entry.Signal} {entry.ArgsJson}")));
}
