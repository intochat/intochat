/*
# UI Gallery
Role: Default
Shipped: true
Operations: open, append, showcaseCommand

The one showcase: working, clickable prototypes of every control.* control and every data node, one page per catalog kind and a page
per chat scenario. Control kinds are real children (child key = the kind); data node kinds are drawn inline from their [Showcase]
examples. The app follows every child's facts and keeps the last 50 in an event log shown on every page: the visible proof each control
signals. Controls are uncontrolled (they keep their own state) except the switch labelled as controlled, whose value the app owns, and
the kanban board, whose cards the app owns: a move the board signals lands in the app's state and the board is drawn again with the card
moved. The query table page shows a table read from a source and one with inline rows the person can select; the form page has a secret
field, which the client stores in the vault and submits only as a reference.
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

namespace IntoChat.OS.Apps.Gallery;

[Neuron("app.gallery")]
[Screen("app.gallery", Title = "UI Gallery", Icon = "layout-grid", Category = "Studio", Dock = 8)]
public sealed class GalleryApp : AggregateScreen<GalleryState>
{
    public const string AppName = "gallery";

    private string _brain = "preview";

    public static AppBundle Bundle => AppBundle.FromScreen<GalleryApp>(
        AppName,
        "4.0.0",
        description: "Working prototypes of every control and data node, with a log of the facts they publish.",
        operations:
        [
            new("open", "Show the page the input names: a scenario id or a catalog kind (the first scenario when empty or unknown)."),
            new("append", "Append the next sample point to the live chart the current page shows."),
            new("showcaseCommand", "Log a command a data node example sent (its kind, signal and args)."),
        ],
        sideWidth: 268d,
        toolbar: [new("open", Icon: "widgets", Label: "Home")]);

    public new GalleryState State => base.State;

    protected override string Title => "UI Gallery";

    /// <summary>The view of the current state, as the screen draws it.</summary>
    public View Render() => PreviewView(State);

    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        AppActivationScope.Notify(this);
        _brain = Id.Brain.Value;
        await base.OnActivate(cancellationToken);
        await FollowChild<IListBoxControl, SelectionChanged>(GalleryPages.Nav);
        await FollowChild<ICheckBoxControl, Toggled>("checkBox");
        await FollowChild<IToggleSwitchControl, Toggled>("toggleSwitch");
        await FollowChild<IToggleSwitchControl, Toggled>(GalleryPages.ControlledSwitch);
        await FollowChild<IExpanderControl, Expanded>("expander");
        await FollowChild<ITabStripControl, SelectionChanged>("tabStrip");
        await FollowChild<IComboBoxControl, SelectionChanged>("comboBox");
        await FollowChild<IComboBoxControl, SelectionChanged>(GalleryPages.ModelPicker);
        await FollowChild<IListBoxControl, SelectionChanged>("listBox");
        await FollowChild<IFormControl, Submitted>("form");
        await FollowChild<ILiveChartControl, ChartChanged>("liveChart");
        await FollowChild<ILiveChartControl, ChartChanged>(GalleryPages.LiveChart);
        await FollowChild<IQueryTableControl, TableChanged>("queryTable");
        await FollowChild<IQueryTableControl, TableChanged>(GalleryPages.TableQuery);
        await FollowChild<IQueryTableControl, TableChanged>(GalleryPages.InlineTable);
        await FollowChild<IQueryTableControl, RowSelected>(GalleryPages.InlineTable);
        await FollowChild<IKanbanControl, SelectionChanged>("kanban");
        await FollowChild<IKanbanControl, CardMoved>("kanban");
        await FollowChild<IStepperControl, SelectionChanged>("stepper");
        await FollowChild<ITreeControl, SelectionChanged>("tree");
        await FollowChild<ITreeControl, NodeExpanded>("tree");
        await FollowChild<ISliderControl, ValueChanged>("slider");
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
        ["chatThread", GalleryPages.Message, GalleryPages.FailedReply, GalleryPages.CardInChat, GalleryPages.ActivityRow, GalleryPages.Composer, GalleryPages.Empty, GalleryPages.Waiting];

    [On]
    public Task Open(GalleryOpen request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ShowPage(GalleryPages.Find(request.Page)?.Id ?? GalleryPages.First, State);
    }

    [On]
    public async Task Append(GalleryAppend request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chart = State.Page switch { "liveChart" => "liveChart", GalleryPages.LiveChart => GalleryPages.LiveChart, _ => null };
        if (chart is null) { return; }
        var number = State.Appended + 1;
        await Show(State with { Appended = number });
        await SendTo(chart, new Append(GalleryPages.Point(number)));
    }

    [On]
    public Task OnSelectionChanged(SelectionChanged changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var logged = Log(changed);
        return SignalChild switch
        {
            GalleryPages.Nav when GalleryPages.Find(changed.Id) is { } page => ShowPage(page.Id, logged),
            GalleryPages.ModelPicker when GalleryPages.Models.Any(model => model.Id == changed.Id) => Show(logged with { Model = changed.Id }),
            _ => Show(logged),
        };
    }

    [On]
    public Task OnToggled(Toggled toggled)
    {
        ArgumentNullException.ThrowIfNull(toggled);
        var logged = Log(toggled);
        return Show(SignalChild == GalleryPages.ControlledSwitch ? logged with { ControlledOn = toggled.IsOn } : logged);
    }

    [On]
    public Task ShowcaseCommand(GalleryShowcaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Show(State.Logged(new GalleryEvent(command.Kind ?? "(example)", command.OriginalSignal ?? "", command.ArgsJson ?? "null", Context.Time.GetUtcNow())));
    }

    [On]
    public Task OnCardMoved(CardMoved moved)
    {
        ArgumentNullException.ThrowIfNull(moved);
        return Show(Log(moved) with { Cards = GalleryPages.Move(State.Cards, moved.CardId, moved.ColumnId, moved.Index) });
    }

    [On] public Task OnExpanded(Expanded expanded) => Show(Log(expanded));
    [On] public Task OnNodeExpanded(NodeExpanded expanded) => Show(Log(expanded));
    [On] public Task OnValueChanged(ValueChanged changed) => Show(Log(changed));
    [On] public Task OnSubmitted(Submitted submitted) => Show(Log(submitted));
    [On] public Task OnChartChanged(ChartChanged changed) => Show(Log(changed));
    [On] public Task OnTableChanged(TableChanged changed) => Show(Log(changed));
    [On] public Task OnRowSelected(RowSelected selected) => Show(Log(selected));
    [On] public Task OnMessageAppended(MessageAppended appended) => Show(Log(appended));
    [On] public Task OnMessageUpdated(MessageUpdated updated) => Show(Log(updated));
    [On] public Task OnRetryRequested(RetryRequested requested) => Show(Log(requested));
    [On] public Task OnStopRequested(StopRequested requested) => Show(Log(requested));

    private GalleryState Log<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        return State.Logged(new GalleryEvent(SignalChild ?? "(outside)", AliasOf(typeof(TSignal)),
            JsonSerializer.Serialize(signal, typeof(TSignal), JsonSerializerOptions.Web), Context.Time.GetUtcNow()));
    }

    // Shows a page; a scenario page then writes its sample messages into its chat thread (idempotent by id, so every show is safe).
    private async Task ShowPage(string page, GalleryState basis)
    {
        await Show(basis with { Page = page });
        if (GalleryPages.Seed(page) is { Count: > 0 } seed) { await SendTo(page, new ImportMessages(seed)); }
        // A chart starts with a few points: the chart ignores an event id it holds, so a later show (or one after Append) adds nothing.
        if (page is "liveChart" or GalleryPages.LiveChart)
        {
            foreach (var point in GalleryPages.ChartSeed) { await SendTo(page, new Append(point)); }
        }
    }

    private static string AliasOf(Type signal) => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<SignalAttribute>(signal)!.Alias;

    private Task SendTo<TCommand>(string slot, TCommand command) where TCommand : Signal
        => Send(new Command(slot, AliasOf(typeof(TCommand)), RuntimeProtocol.SerializeToElement(command, typeof(TCommand))));

    protected override Node Render(GalleryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var page = GalleryPages.Find(state.Page) ?? GalleryPages.All[0];
        var nav = Child<IListBoxControl, ListBoxProps>(GalleryPages.Nav, new ListBoxProps(
            [.. GalleryPages.All.Select(item => new ListBoxItem(item.Id, item.Title, Group: item.Group))], AutomationName: "Gallery pages", SelectedId: page.Id));
        return new SplitPane(nav, new Column(
        [
            new Heading(page.Title, 1, page.Group),
            .. Page(page, state),
            new Divider("Event log"),
            .. EventLog(state),
        ]));
    }

    private IEnumerable<Node> Page(GalleryPage page, GalleryState state)
    {
        if (page.Group == GalleryPages.DataNode)
        {
            var examples = GalleryPages.Examples(page.Id, GalleryPages.NodeKinds[page.Id]);
            return examples.Count > 0 ? examples : [new Text($"The {page.Id} node has no [Showcase] example yet.")];
        }
        return page.Id switch
        {
            "checkBox" => [Child<ICheckBoxControl, CheckBoxProps>("checkBox", new CheckBoxProps("Remember me"))],
            "toggleSwitch" =>
            [
                Child<IToggleSwitchControl, ToggleSwitchProps>("toggleSwitch", new ToggleSwitchProps("Wi-Fi (uncontrolled: the switch keeps its value)", "On", "Off")),
                Child<IToggleSwitchControl, ToggleSwitchProps>(GalleryPages.ControlledSwitch,
                    new ToggleSwitchProps("Controlled: the Gallery owns this value", "On", "Off", IsOn: state.ControlledOn)),
                new Text(state.ControlledOn ? "The Gallery stored: on" : "The Gallery stored: off"),
            ],
            "expander" => [Child<IExpanderControl, ExpanderProps>("expander", new ExpanderProps("Details", [new Text("Shown while expanded.")]))],
            "tabStrip" => [Child<ITabStripControl, TabStripProps>("tabStrip", new TabStripProps([new("home", "Home"), new("files", "Files"), new("share", "Share")]))],
            "comboBox" => [Child<IComboBoxControl, ComboBoxProps>("comboBox", new ComboBoxProps("Theme", [new("light", "Light"), new("dark", "Dark"), new("auto", "Auto")]))],
            "listBox" => [Child<IListBoxControl, ListBoxProps>("listBox", new ListBoxProps([new("ana", "Ana", "Design"), new("ben", "Ben", "Research"), new("cy", "Cy")]))],
            "form" => [Child<IFormControl, FormProps>("form", new FormProps(
                [
                    new FormInput("name", "Name", Placeholder: "Your name", MaxLength: 40), new FormInput("age", "Age", FormFieldKind.Number),
                    new FormInput("bio", "Bio", FormFieldKind.Multiline),
                    new FormInput(GalleryPages.SecretField, "API key", FormFieldKind.Secret, Placeholder: "Stored in your vault; only its reference is sent"),
                ],
                "Save"))],
            "liveChart" or GalleryPages.LiveChart => [Chart(page.Id)],
            "queryTable" =>
            [
                SourceTable(page.Id),
                new Text("Inline rows: the app gives the rows, the person selects one."),
                Child<IQueryTableControl, QueryTableProps>(GalleryPages.InlineTable, new QueryTableProps("Release tasks", GalleryPages.InlineColumns,
                    Rows: GalleryPages.InlineRows, AutomationName: "Release tasks")),
            ],
            GalleryPages.TableQuery => [SourceTable(page.Id)],
            "kanban" => [Child<IKanbanControl, KanbanProps>("kanban", new KanbanProps("Release", GalleryPages.Columns, state.Cards, AutomationName: "Release board"))],
            "stepper" => [Child<IStepperControl, StepperProps>("stepper", new StepperProps(
                [new("plan", "Plan", StepperStatus.Done), new("build", "Build", StepperStatus.Active, "Compiling"), new("ship", "Ship")], "Release"))],
            "tree" => [Child<ITreeControl, TreeProps>("tree", new TreeProps(
                [new("src", "src", IsFolder: true), new("app", "app.cs", "src"), new("docs", "docs", IsFolder: true), new("guide", "guide.md", "docs"), new("readme", "README.md")],
                AutomationName: "Files"))],
            "slider" => [Child<ISliderControl, SliderProps>("slider", new SliderProps("Volume", 0, 100, 5, "%"))],
            "chatThread" => [Thread("chatThread", new ChatThreadProps("Write a message", AutomationName: "Chat thread"))],
            GalleryPages.Message => [Thread(page.Id, new ChatThreadProps(AutomationName: "Message"))],
            GalleryPages.FailedReply => [Thread(page.Id, new ChatThreadProps(Status: "failed", AutomationName: "Failed reply"))],
            GalleryPages.CardInChat => [Thread(page.Id, new ChatThreadProps(AutomationName: "Card in chat"))],
            GalleryPages.ActivityRow => [Thread(page.Id, new ChatThreadProps(AutomationName: "Activity row"))],
            GalleryPages.Composer =>
            [
                Child<IComboBoxControl, ComboBoxProps>(GalleryPages.ModelPicker, new ComboBoxProps("Model", GalleryPages.Models)),
                Thread(page.Id, new ChatThreadProps("Message the model", ActiveModelLabel: GalleryPages.Models.First(model => model.Id == state.Model).Label, AutomationName: "Composer")),
            ],
            GalleryPages.Empty => [Thread(page.Id, new ChatThreadProps(EmptyText: "Ask anything to start.", AutomationName: "Empty"))],
            GalleryPages.Waiting => [Thread(page.Id, new ChatThreadProps(CanSend: false, Status: "working", Working: true, AutomationName: "Waiting"))],
            _ => [new Text($"The {page.Id} control has no page yet.")],
        };
    }

    private Node Thread(string key, ChatThreadProps props) => Child<IChatThreadControl, ChatThreadProps>(key, props);

    private Node SourceTable(string key) => Child<IQueryTableControl, QueryTableProps>(key, new QueryTableProps("People",
        [new("id", "Id"), new("name", "Name"), new("status", "Status")], $"{_brain}/control.sampleTableSource/{GalleryPages.SampleSourceKey}"));

    private Node Chart(string key) => new Column(
    [
        Child<ILiveChartControl, LiveChartProps>(key, new LiveChartProps("Live points")),
        new Button("append", "Append point", Sends(new GalleryAppend()), Tone: ButtonTone.Accent, AutomationName: "Append point")
        {
            Style = OsMixStyles.PrimaryButton(),
        },
    ]);

    private static IEnumerable<Node> EventLog(GalleryState state)
        => state.Log.Length == 0
            ? [new Text("No signals yet: use a control.")]
            : state.Log.Reverse().Select(entry => (Node)new Text(string.Create(CultureInfo.InvariantCulture,
                $"{entry.At:HH:mm:ss} {entry.Control} {entry.Signal} {entry.ArgsJson}")));
}
