using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Form;

[Collection(FlutterHostCollection.Name)]
public sealed class FormHttpFacts(FlutterHostFixture host)
{
    [Fact]
    public async Task GetReturnsTheSubmittedValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        using var person = await host.PersonAsync(ct);
        var ws = person.BrainId;
        using var caller = person.AsCaller();
        var form = brain.Get<IForm>(UiScope.Key(ws, "intake"));
        var defined = await form.Define(new("Customer intake",
        [
            new("name", "Name", FormFieldKind.PlainText, true),
            new("birthDate", "Date of birth", FormFieldKind.Date, true),
        ]));
        await form.Submit(new([new("name", "Ada"), new("birthDate", "1815-12-10")], defined.Revision));

        var state = await person.Http.GetFromJsonAsync<FormState>($"/brains/{ws}/ui/forms/intake",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);

        Assert.NotNull(state);
        Assert.True(state!.Submitted);
        Assert.Equal("Ada", state.Fields.Single(field => field.Name == "name").Value);
        Assert.Equal("1815-12-10", state.Fields.Single(field => field.Name == "birthDate").Value);
    }
}
