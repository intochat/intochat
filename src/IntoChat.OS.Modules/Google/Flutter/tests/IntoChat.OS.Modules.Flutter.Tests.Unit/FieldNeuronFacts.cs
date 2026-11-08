using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Spi;
using DigitalBrain.UI;
using IntoChat.OS.Modules.Flutter.Fields;
using IntoChat.OS.Modules.Flutter.Views;
using Microsoft.AspNetCore.Http;
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

    private static IDisposable As(string app) => RuntimeCallerContext.Stamp(new CapabilityCaller("p1", "a1", "owner", AppId: "owner/apps/" + app));

    [Fact]
    public async Task InputJournalsFieldChangedWithoutTheValue()
    {
        await using var os = Started();
        var field = os.Get<IField>(Title);

        await field.Configure(FieldKind.Text);
        Assert.DoesNotContain(os.Journal(Owner).Signals(Title), entry => entry.Signal is FieldChanged);
        await field.Input("Ship docs");

        var changed = Assert.Single(os.Journal(Owner).Signals(Title).Select(entry => entry.Signal).OfType<FieldChanged>());
        Assert.Equal(new FieldChanged("owner/apps/tasks", "title", 2), changed);
        var state = await field.Read();
        Assert.Equal(("owner/apps/tasks", "title", FieldKind.Text, "Ship docs", 2L), (state.Owner, state.Name, state.Kind, state.Value, state.Version));
    }

    [Fact]
    public async Task InputIsRefusedBeforeTheFieldIsConfigured()
    {
        await using var os = Started();

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => os.Get<IField>(Title).Input("early"));

        Assert.Contains("not configured", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await os.Get<IField>(Title).Read()).Version);
    }

    [Fact]
    public async Task AFieldHoldsAtMost32000Characters()
    {
        await using var os = Started();
        var field = os.Get<IField>(Title);
        await field.Configure(FieldKind.Multiline);

        await field.Input(new string('a', 32_000));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => field.Input(new string('a', 32_001)));

        Assert.Equal(32_000, (await field.Read()).Value!.Length);
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
    public async Task TheOwnerTakesASecretOnce()
    {
        await using var os = Started();
        var field = os.Get<IField>(Secret);
        await field.Configure(FieldKind.Password);
        await field.Input("hunter2");

        using (As("tasks"))
        {
            Assert.Equal("hunter2", await field.TakeSecret());
            Assert.Null(await field.TakeSecret());
        }
        Assert.Equal(FieldNeuron.SecretSetMarker, (await field.Read()).Value);
    }

    [Fact]
    public async Task ASecretIsGoneAfterDeactivation()
    {
        await using var os = Started();
        var field = os.Get<IField>(Secret);
        await field.Configure(FieldKind.Password);
        await field.Input("hunter2");

        await os.Deactivate(Secret);

        using (As("tasks")) { Assert.Null(await field.TakeSecret()); }
        Assert.Equal(FieldNeuron.SecretSetMarker, (await field.Read()).Value);
    }

    [Fact]
    public async Task AnotherAppCannotTakeASecret()
    {
        await using var os = Started();
        var field = os.Get<IField>(Secret);
        await field.Configure(FieldKind.Password);
        await field.Input("hunter2");

        using (As("files")) { await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(field.TakeSecret); }

        using (As("tasks")) { Assert.Equal("hunter2", await field.TakeSecret()); }
    }

    [Fact]
    public async Task AnotherAppCannotSetOrConfigureAField()
    {
        await using var os = Started();
        var field = os.Get<IField>(Title);
        await field.Configure(FieldKind.Text);
        await field.Set("mine");
        var journaled = os.Journal(Owner).Signals(Title).Count;

        using (As("files"))
        {
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => field.Set("theirs"));
            await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => field.Configure(FieldKind.Secret));
        }

        var state = await field.Read();
        Assert.Equal((FieldKind.Text, "mine", 2L), (state.Kind, state.Value, state.Version));
        Assert.Equal(journaled, os.Journal(Owner).Signals(Title).Count);
        using (As("tasks")) { await field.Set("still mine"); }
        Assert.Equal("still mine", (await field.Read()).Value);
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
            new Field("dots", "apps/tasks/../files/name", FieldKind.Password),
            new Field("hidden", "apps/tasks/.hidden", FieldKind.Password),
        ])));

        var token = await os.Get<IField>(Secret).Read();
        Assert.Equal(FieldKind.Password, token.Kind);
        Assert.Null(token.Value);
        Assert.DoesNotContain(os.Journal(Owner).Signals(Secret), entry => entry.Signal is FieldChanged);
        // Another app's field, or a name that escapes the app's prefix, is not the view's to configure.
        Assert.Equal(0, (await os.Get<IField>(new NeuronId(Owner, UIVocabulary.FieldType, "apps/files/name")).Read()).Version);
        Assert.Equal(0, (await os.Get<IField>(new NeuronId(Owner, UIVocabulary.FieldType, "apps/tasks/../files/name")).Read()).Version);
        Assert.Equal(0, (await os.Get<IField>(new NeuronId(Owner, UIVocabulary.FieldType, "apps/tasks/.hidden")).Read()).Version);
    }

    [Fact]
    public async Task TheEdgeWritesAFieldByKindField()
    {
        await using var os = Started();
        await os.Get<IField>(Title).Configure(FieldKind.Text);

        await AppUiEndpoints.Deliver(new AppUiEndpoints.UiEvent("field", "owner/apps/tasks/title", Value: "Ship docs"), new Brain(os), "owner", null, Ct);

        Assert.Equal("Ship docs", (await os.Get<IField>(Title).Read()).Value);
    }

    [Fact]
    public async Task TheEdgeReadsAFieldByKindField()
    {
        await using var os = Started();
        await os.Get<IField>(Title).Configure(FieldKind.Text);
        await os.Get<IField>(Title).Set("Ada");

        var result = await AppUiEndpoints.ReadNode("field", "owner/apps/tasks/title", new Brain(os), "owner", Ct);

        var state = Assert.IsType<FieldState>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);
        Assert.Equal(("owner/apps/tasks", "title", "Ada"), (state.Owner, state.Name, state.Value));
    }

    private sealed class Brain(TestOS os) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => os.Get<T>(id);
    }
}
