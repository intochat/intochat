using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Modules.UI.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

// Stored control state is keyed by the neuron type id (control.*) and holds the plain state record as JSON. Each row is the JSON a
// brain holds today for that type; a control that moves assembly or namespace still reads it, so a person's checked box, selected
// tab, submitted form or chat history survives the move.
public sealed class StoredControlStateFacts
{
    public static TheoryData<string> TypeIds => [.. Rows.Keys];

    private static readonly IReadOnlyDictionary<string, (Type Neuron, string Json, Action<Node> Check)> Rows =
        new Dictionary<string, (Type, string, Action<Node>)>(StringComparer.Ordinal)
        {
            ["control.checkBox"] = (typeof(CheckBoxNeuron), """{"isOn":true}""", node => Assert.True(Assert.IsType<CheckBoxView>(node).IsOn)),
            ["control.toggleSwitch"] = (typeof(ToggleSwitchNeuron), """{"isOn":true}""", node => Assert.True(Assert.IsType<ToggleSwitchView>(node).IsOn)),
            ["control.expander"] = (typeof(ExpanderNeuron), """{"isExpanded":true}""", node => Assert.True(Assert.IsType<ExpanderView>(node).IsExpanded)),
            ["control.tabStrip"] = (typeof(TabStripNeuron), """{"selectedId":"files"}""", node => Assert.Equal("files", Assert.IsType<TabStripView>(node).SelectedId)),
            ["control.comboBox"] = (typeof(ComboBoxNeuron), """{"selectedId":"dark"}""", node => Assert.Equal("dark", Assert.IsType<ComboBoxView>(node).SelectedId)),
            ["control.listBox"] = (typeof(ListBoxNeuron), """{"selectedId":"ben"}""", node => Assert.Equal("ben", Assert.IsType<ListBoxView>(node).SelectedId)),
            ["control.form"] = (typeof(FormNeuron), """{"values":{"name":"Ada"}}""",
                node => Assert.Equal([new FormValue("name", "Ada")], Assert.IsType<FormView>(node).Values)),
            ["control.liveChart"] = (typeof(LiveChartNeuron), """{"version":3,"points":[{"eventId":"e1","label":"Mon","value":2}]}""",
                node =>
                {
                    var chart = Assert.IsType<LiveChartView>(node);
                    Assert.Equal(3, chart.Version);
                    Assert.Equal([new ChartPointNode("Mon", 2)], chart.Points);
                }
            ),
            ["control.queryTable"] = (typeof(QueryTableNeuron), """{"filter":"ann","sortBy":"name","descending":true,"page":2}""",
                node =>
                {
                    var table = Assert.IsType<QueryTableView>(node);
                    Assert.Equal(("ann", "name", true, 2), (table.Filter, table.SortBy, table.Descending, table.Page));
                }
            ),
            ["control.chatThread"] = (typeof(ChatThreadNeuron),
                """{"messages":[{"id":"m1","role":"user","text":"hello","status":"completed","activities":[],"cards":[]}],"showsStop":false,"showsRetry":false,"acceptsText":true}""",
                node => Assert.Equal("hello", Assert.Single(Assert.IsType<ChatThreadView>(node).Messages).Text)),
        };

    [Fact]
    public void Every_control_neuron_has_a_stored_state_row()
        => Assert.Equal(
            ControlSamples.All.Keys.Select(type => type.GetCustomAttribute<NeuronAttribute>()!.Type).Order(StringComparer.Ordinal),
            Rows.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(TypeIds))]
    public async Task A_control_reads_the_state_a_brain_already_holds(string typeId)
    {
        var (neuron, json, check) = Rows[typeId];
        Assert.Equal(typeId, neuron.GetCustomAttribute<NeuronAttribute>()!.Type);
        await using var os = TestOS.Local();
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", ControlHarness.Brain));
        typeof(TestOS).GetMethod(nameof(TestOS.Register), Type.EmptyTypes)!.MakeGenericMethod(neuron).Invoke(os, null);
        var id = new NeuronId(ControlHarness.Brain, typeId, "app.uikit/default/stored");
        os.SeedStoredState(id, json);

        var view = await os.Get<IScreen>(id).Read() ?? throw new InvalidOperationException($"{id} has no view.");

        check(view.View.GetProperty("root").Deserialize<Node>(JsonSerializerOptions.Web)!);
    }
}
