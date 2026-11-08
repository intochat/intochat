using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Spi;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Flutter.Fields;
using IntoChat.OS.Modules.Flutter.Views;
using TestOS = DigitalBrain.Testing.TestOS;

namespace IntoChat.OS.Modules.Flutter;

// The field neuron on TestOS: one view field's value at {brain}/apps/{app}/{field}; the view carries none.
public sealed class FieldNeuronFacts
{
    private static readonly BrainId Owner = new("owner");

    private static readonly NeuronId Title = new(Owner, UIVocabulary.FieldType, "apps/tasks/title");

    private static readonly NeuronId Secret = new(Owner, UIVocabulary.FieldType, "apps/tasks/token");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TestOS Started() => TestOS.Local().Register(typeof(FlutterModule).Assembly);

    [Fact]
    public async Task InputJournalsFieldChangedWithoutTheValue()
    {
        await using var os = Started();
        var field = os.Get<IField>(Title);

        await field.Input("Ship docs");

        var changed = Assert.IsType<FieldChanged>(Assert.Single(os.Journal(Owner).Signals(Title)).Signal);
        Assert.Equal(new FieldChanged("owner/apps/tasks", "title", 1), changed);
        var state = await field.Read();
        Assert.Equal(("owner/apps/tasks", "title", FieldKind.Text, "Ship docs", 1L), (state.Owner, state.Name, state.Kind, state.Value, state.Version));
    }

    [Fact]
    public async Task ASecretValueIsNeverPersisted()
    {
        await using var os = Started();
        var field = os.Get<IField>(Secret);

        await field.Set("plain-before");
        await field.Configure(FieldKind.Secret);
        Assert.Equal(FieldNeuron.SecretSetMarker, (await field.Read()).Value);
        await field.Input("hunter2");

        Assert.Equal(FieldNeuron.SecretSetMarker, (await field.Read()).Value);
        Assert.DoesNotContain(os.Journal(Owner).Signals(Secret), entry => JsonSerializer.Serialize(entry.Signal, entry.Signal.GetType()).Contains("hunter2", StringComparison.Ordinal));
        // The marker is not a value: a secret that becomes plain starts empty.
        await field.Configure(FieldKind.Text);
        Assert.Equal("", (await field.Read()).Value);
    }

    [Fact]
    public async Task SetPreFillsAndClears()
    {
        await using var os = Started();
        var field = os.Get<IField>(Title);

        await field.Set("Ada");
        Assert.Equal("Ada", (await field.Read()).Value);
        await field.Set(null);
        Assert.Null((await field.Read()).Value);
        Assert.Equal(2, (await field.Read()).Version);
    }

    [Fact]
    public async Task PublishingAViewConfiguresItsOwnAppsFieldsAndSeedsNoValue()
    {
        await using var os = Started();
        var view = os.Get<IView>(new NeuronId(Owner, UIVocabulary.ViewType, "apps/tasks/view"));

        await view.Publish(1, new View("Tasks", new Column(
        [
            new Field("token", "apps/tasks/token", FieldKind.Password),
            new Field("other", "apps/files/name", FieldKind.Password),
        ])));

        var token = await os.Get<IField>(Secret).Read();
        Assert.Equal(FieldKind.Password, token.Kind);
        Assert.Null(token.Value);
        // Another app's field is not the view's to configure.
        Assert.Equal(0, (await os.Get<IField>(new NeuronId(Owner, UIVocabulary.FieldType, "apps/files/name")).Read()).Version);
    }

    [Fact]
    public async Task TheEdgeReadsAndWritesAFieldByKindField()
    {
        await using var os = Started();
        var brain = new Brain(os);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("field", "owner/apps/tasks/title", Value: "Ship docs"), brain, "owner", null, Ct);

        Assert.Equal("Ship docs", (await os.Get<IField>(Title).Read()).Value);
    }

    private sealed class Brain(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }
}
