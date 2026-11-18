using DigitalBrain.UI;
using DigitalBrain.UI.Commands;
using DigitalBrain.UI.Controls;
using IntoChat.OS.Apps.Controls;

namespace IntoChat.OS.Apps.Tests.Unit.Controls;

public sealed class FormNeuronFacts
{
    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static Submitted Single(ControlHarness form) => Assert.IsType<Submitted>(Assert.Single(form.Published()));

    [Fact]
    public async Task Submit_keeps_the_values_and_signals_Submitted()
    {
        await using var form = new ControlHarness(typeof(FormNeuron), key: "profile");

        await form.Send(new Submit(Values(("name", "Ada"), ("bio", "Engineer"))));

        Assert.Equal(Values(("bio", "Engineer"), ("name", "Ada")), Single(form).Values);
        Assert.Equal(new FormView("profile", [new FormValue("bio", "Engineer"), new FormValue("name", "Ada")]), await form.Drawn<FormView>());
    }

    [Fact]
    public async Task Submit_sends_all_values_including_untouched()
    {
        await using var form = new ControlHarness(typeof(FormNeuron), state: new FormState { Values = Values(("name", "Ada"), ("bio", "Engineer")) });

        await form.Send(new Submit(Values(("bio", "Pilot"))));

        Assert.Equal(Values(("name", "Ada"), ("bio", "Pilot")), Single(form).Values);
    }

    [Fact]
    public async Task A_secret_is_passed_on_but_never_kept_or_drawn()
    {
        await using var form = new ControlHarness(typeof(FormNeuron), key: "keys");

        await form.Send(new Submit(Values(("label", "Work"), ("token", "sk-secret")), ["token"]));

        Assert.Equal("sk-secret", Single(form).Values["token"]);
        Assert.Equal(new FormView("keys", [new FormValue("label", "Work")]), await form.Drawn<FormView>());
    }
}
