using IntoChat.OS.Flutter;
using IntoChat.OS.Flutter.Form;
using IntoChat.OS.Flutter.Layout;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Composition;

public sealed class FormCompositionFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task ALayoutCanEmbedAnExistingFormWithoutResettingItsDraft()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("owner/apps/circle/name");
        await form.Define(new("Your copy", [new("name", "Name", FormFieldKind.PlainText)]));
        await form.SetDraft("name", "my-echo");
        var layout = brain.Get<ILayout>("owner/apps/circle/content");
        await layout.Set(new("list", [new("form", "owner/apps/circle/name")]), 0);
        await brain.DeactivateAsync(layout, ct);
        Assert.Equal("form", Assert.Single((await layout.Read()).Definition.Children).Kind);
        Assert.Equal("my-echo", Assert.Single((await form.Read()).Fields).Value);
    }
}
