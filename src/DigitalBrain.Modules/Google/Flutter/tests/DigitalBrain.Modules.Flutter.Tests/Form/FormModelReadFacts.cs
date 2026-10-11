using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Form;
using DigitalBrain.Framework;

namespace DigitalBrain.Modules.Flutter.Tests.Form;

public sealed class FormModelReadFacts
{
    private const string Brain = "intake";
    private const string FormKey = Brain + "/apps/forms/intake";

    [Fact]
    public async Task TheModelReadsAFormsShapeButNeverItsValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        StampApp(conversation: null);
        var form = brain.Get<IForm>(FormKey);
        await form.Define(new("Intake", [new("name", "Name", FormFieldKind.PlainText, Required: true), new("token", "Token", FormFieldKind.Secret)]));
        await form.SetDraft("name", "Alice");
        var secret = SecretReferences.For("owner", "intake-token", "Token", isSet: true);
        await form.SetSecret("token", new FormSecret(secret.Reference, secret.Label, true));

        StampApp(conversation: Brain + "/agents/assistant");
        var seen = await form.Read();
        var json = JsonSerializer.Serialize(seen);
        Assert.DoesNotContain("Alice", json);
        Assert.DoesNotContain(secret.Reference, json);
        Assert.Equal(["name", "token"], seen.Fields.Select(field => field.Name));
        Assert.True(seen.Fields[0].Required);

        StampApp(conversation: null);
        Assert.Contains("Alice", JsonSerializer.Serialize(await form.Read()));
    }

    private static void StampApp(string? conversation) => CallerContextStamper.Stamp(new CallerContext
    {
        PrincipalId = "owner",
        AccountId = "owner",
        BrainId = Brain,
        Kind = CallerKind.App,
        StampedBy = TrustedEdge.AppProxy,
        AppId = Brain + "/apps/forms",
        ConversationId = conversation,
    });
}
