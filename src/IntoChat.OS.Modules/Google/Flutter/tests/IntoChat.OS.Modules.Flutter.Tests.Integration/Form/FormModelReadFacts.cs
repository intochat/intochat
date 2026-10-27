using System.Text.Json;
using IntoChat.OS.Modules.Flutter.Form;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Form;

public sealed class FormModelReadFacts
{
    [Trait("host", "own")]
    [Fact]
    public async Task TheModelReadsAFormsShapeButNeverItsValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var owner = brain.Owner;
        var app = owner.Key("apps/forms");
        var form = brain.Get<IForm>(owner.Key("apps/forms/intake"));
        var secret = SecretReferences.For(owner.AccountId, "intake-token", "Token", isSet: true);
        using (owner.AsApp(app))
        {
            await form.Define(new("Intake", [new("name", "Name", FormFieldKind.PlainText, Required: true), new("token", "Token", FormFieldKind.Secret)]));
            await form.SetDraft("name", "Alice");
            await form.SetSecret("token", new FormSecret(secret.Reference, secret.Label, true));
        }

        using (owner.AsApp(app, owner.Key("agents/assistant")))
        {
            var seen = await form.Read();
            var json = JsonSerializer.Serialize(seen);
            Assert.DoesNotContain("Alice", json);
            Assert.DoesNotContain(secret.Reference, json);
            Assert.Equal(["name", "token"], seen.Fields.Select(field => field.Name));
            Assert.True(seen.Fields[0].Required);
        }

        using (owner.AsApp(app)) { Assert.Contains("Alice", JsonSerializer.Serialize(await form.Read())); }
    }
}
