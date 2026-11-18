using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.Units;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed record FormParentState
{
    public bool Locked { get; init; }
}

/*
# Form parent

A test parent that slots a profile form: a capped name, a number, a read-only id and a secret.
*/
[Neuron("test.form-parent")]
public sealed class FormParent : Screen<FormParentState>
{
    protected override View Render(FormParentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new("Parent", new Column([
            Slot<FormNeuron, FormProps>("profile", new FormProps([
                new FormInput("name", "Name", MaxLength: 5),
                new FormInput("age", "Age", FormFieldKind.Number),
                new FormInput("id", "Id", Value: "ada-1", IsReadOnly: true),
                new FormInput("token", "Token", FormFieldKind.Secret),
            ], "Save", IsEnabled: !state.Locked)),
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

    private Task Submit(Dictionary<string, string> values, params string[] secrets)
        => _os.Get<IScreen>(ParentId).Dispatch(new Command("profile", "control.submit",
            JsonSerializer.SerializeToElement(new Submit(values, secrets.Length == 0 ? null : secrets), JsonSerializerOptions.Web)));

    private async Task<string> Refusal(Dictionary<string, string> values, params string[] secrets)
        => (await Assert.ThrowsAsync<ActivationRefusedException>(() => Submit(values, secrets))).Reason;

    [Fact]
    public async Task A_valid_submit_reaches_the_form()
    {
        await Submit(new() { ["name"] = "Ada", ["age"] = "36", ["token"] = "sk" }, "token");

        var submitted = Assert.IsType<Submitted>(Assert.Single(_os.Journal(Brain).Signals(FormId).Select(entry => entry.Signal),
            signal => signal is not StateChanged && signal is not ViewChanged));
        Assert.Equal("sk", submitted.Values["token"]);
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
    public async Task A_secret_must_be_declared()
        => Assert.Equal(FormRefusals.UndeclaredSecret, await Refusal(new() { ["token"] = "sk" }));

    public async ValueTask DisposeAsync()
    {
        await _os.DisposeAsync();
        _person.Dispose();
    }
}
