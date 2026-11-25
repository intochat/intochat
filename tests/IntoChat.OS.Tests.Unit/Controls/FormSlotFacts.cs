using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities.Secrets;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.Units;
using IntoChat.OS.Modules.UI.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record FormParentState
{
    public bool Locked { get; init; }
}

/*
# Form parent

A test parent that slots a form (a capped name, a number, a read-only id) and a second form with a label and a secret field.
*/
[Neuron("test.form-parent")]
public sealed class FormParent : Screen<FormParentState>
{
    protected override View Render(FormParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Child<FormNeuron, FormProps>("profile", new FormProps([
                new FormInput("name", "Name", MaxLength: 5),
                new FormInput("age", "Age", FormFieldKind.Number),
                new FormInput("id", "Id", Value: "ada-1", IsReadOnly: true),
            ], "Save", IsEnabled: !state.Locked)),
            Child<FormNeuron, FormProps>("keys", new FormProps([new FormInput("label", "Label"), new FormInput("token", "Token", FormFieldKind.Secret)], "Seal")),
        ]));
    }
}

// The fields are the parent's props, so the parent's routing hop checks a submit against them with a coded reason before the form is reached.
public sealed class FormSlotFacts : IAsyncDisposable
{
    private static readonly BrainId Brain = new("local");
    private static readonly NeuronId ParentId = new(Brain, "test.form-parent", "main");
    private static readonly NeuronId FormId = new(Brain, "control.form", ScreenExtensions.ChildAddress(ParentId, "profile"));

    private readonly IDisposable _person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("local", "local", Brain));
    private readonly TestOS _os = TestOS.Local();

    public FormSlotFacts()
    {
        _os.Register<FormParent>();
        _os.Register<FormNeuron>();
    }

    private static readonly NeuronId KeysId = new(Brain, "control.form", ScreenExtensions.ChildAddress(ParentId, "keys"));

    // What the client submits after storing the typed secret through POST /secrets/{owner}: the vault's reference.
    private static readonly string TokenReference = SecretReferences.For("local", "local/apps/test/keys/token", "Token", isSet: true).Reference;

    private Task Submit(Dictionary<string, string> values, string form = "profile", Dictionary<string, string>? secretRefs = null)
        => _os.Get<IScreen>(ParentId).Dispatch(new Command(form, "control.submit",
            JsonSerializer.SerializeToElement(new Submit(values, secretRefs), JsonSerializerOptions.Web)));

    private async Task<string> Refusal(Dictionary<string, string> values, string form = "profile", Dictionary<string, string>? secretRefs = null)
        => (await Assert.ThrowsAsync<ActivationRefusedException>(() => Submit(values, form, secretRefs))).Reason;

    private bool JournalContains(string text)
    {
        var journal = _os.Journal(Brain);
        return new[] { ParentId, FormId, KeysId }.SelectMany(journal.Signals)
            .Any(entry => JsonSerializer.Serialize(entry.Signal, entry.Signal.GetType()).Contains(text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_valid_submit_reaches_the_form()
    {
        await Submit(new() { ["name"] = "Ada", ["age"] = "36" });

        var submitted = Assert.IsType<Submitted>(Assert.Single(_os.Journal(Brain).Signals(FormId).Select(entry => entry.Signal),
            signal => signal is not StateChanged && signal is not ViewChanged));
        Assert.Equal("Ada", submitted.Values["name"]);
    }

    [Fact]
    public async Task An_unknown_field_is_refused()
        => Assert.Equal(FormRefusals.UnknownField, await Refusal(new() { ["nickname"] = "A" }));

    [Fact]
    public async Task A_read_only_field_is_not_overwritten()
    {
        Assert.Equal(FormRefusals.ReadOnlyField, await Refusal(new() { ["id"] = "mallory" }));
        Assert.False(_os.IsActive(FormId));
    }

    [Fact]
    public async Task A_value_over_MaxLength_is_refused()
        => Assert.Equal(FormRefusals.TooLong, await Refusal(new() { ["name"] = "Adalovelace" }));

    [Fact]
    public async Task A_number_field_refuses_text()
        => Assert.Equal(FormRefusals.NotANumber, await Refusal(new() { ["age"] = "old" }));

    [Fact]
    public async Task A_secret_value_inside_values_is_refused_and_never_journaled()
    {
        Assert.Equal(FormRefusals.SecretInValues, await Refusal(new() { ["label"] = "Work", ["token"] = "sk-secret" }, "keys"));

        Assert.False(_os.IsActive(KeysId));
        Assert.False(JournalContains("sk-secret"));
    }

    [Fact]
    public async Task A_secret_reference_reaches_the_form_and_only_the_reference_is_journaled()
    {
        await Submit(new() { ["label"] = "Work" }, "keys", new() { ["token"] = TokenReference });

        var submitted = Assert.IsType<Submitted>(Assert.Single(_os.Journal(Brain).Signals(KeysId).Select(entry => entry.Signal),
            signal => signal is not StateChanged && signal is not ViewChanged));
        Assert.Equal("Work", submitted.Values["label"]);
        Assert.False(submitted.Values.ContainsKey("token"));
        Assert.NotNull(submitted.SecretRefs);
        Assert.Equal(TokenReference, submitted.SecretRefs["token"]);
    }

    [Fact]
    public async Task A_form_with_a_secret_field_accepts_a_submit_that_leaves_the_secret_out()
    {
        await Submit(new() { ["label"] = "Work" }, "keys");

        Assert.True(_os.IsActive(KeysId));
    }

    [Fact]
    public async Task A_secret_reference_for_a_plain_field_is_refused()
        => Assert.Equal(FormRefusals.NotASecretField, await Refusal(new() { ["label"] = "Work" }, "keys", new() { ["label"] = TokenReference }));

    [Fact]
    public async Task A_secret_reference_for_an_unknown_field_is_refused()
        => Assert.Equal(FormRefusals.UnknownField, await Refusal(new() { ["label"] = "Work" }, "keys", new() { ["pin"] = TokenReference }));

    [Fact]
    public async Task A_raw_secret_in_place_of_a_reference_is_refused_and_never_journaled()
    {
        Assert.Equal(FormRefusals.NotASecretReference, await Refusal(new() { ["label"] = "Work" }, "keys", new() { ["token"] = "sk-secret" }));

        Assert.False(_os.IsActive(KeysId));
        Assert.False(JournalContains("sk-secret"));
    }

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
