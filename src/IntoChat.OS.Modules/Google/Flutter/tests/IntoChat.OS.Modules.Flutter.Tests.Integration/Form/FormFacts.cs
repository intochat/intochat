using IntoChat.OS.Modules.Flutter.Form;
using IntoChat.OS.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Form;

public sealed class FormFacts
{
    private static readonly FormDefinition Intake = new("Customer intake",
    [
        new("name", "Name", FormFieldKind.PlainText, Required: true),
        new("surname", "Surname", FormFieldKind.PlainText, Required: true),
        new("birthDate", "Date of birth", FormFieldKind.Date, Required: true),
    ]);

    [Trait("host", "own")]
    [Fact]
    public async Task ASubmitAgainstAStaleFormRevisionIsRefusedWithTheCurrentOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("owner/a/apps/forms/stale");
        var defined = await form.Define(Intake);

        var conflict = await Assert.ThrowsAsync<RevisionConflictException>(() => form.Submit(new([new("name", "Ada")], defined.Revision + 5)));

        Assert.Equal(defined.Revision, conflict.CurrentRevision);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task DefineAndMultiFieldSubmitLandInOneWriteEach()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("owner/a/apps/forms/intake");
        var defined = await form.Define(Intake);
        Assert.Equal(1, defined.Revision);
        Assert.False(defined.Submitted);

        var submitted = await form.Submit(new(
        [
            new("name", "Ada"),
            new("surname", "Lovelace"),
            new("birthDate", "1815-12-10"),
        ], defined.Revision));

        // Three fields, one revision: the submit is a single atomic state write.
        Assert.Equal(2, submitted.Revision);
        Assert.True(submitted.Submitted);
        Assert.Equal("Ada", submitted.Fields.Single(f => f.Name == "name").Value);
        Assert.Equal("1815-12-10", submitted.Fields.Single(f => f.Name == "birthDate").Value);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task InvalidDateNamesTheAllowedValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("owner/a/apps/forms/intake");
        await form.Define(Intake);
        var error = await Assert.ThrowsAsync<FormValidationException>(() => form.Submit(new(
        [
            new("name", "Ada"),
            new("surname", "Lovelace"),
            new("birthDate", "date"),
        ])));
        Assert.Equal(FormFieldKind.Date, error.Kind);
        Assert.Contains("yyyy-MM-dd", error.Message);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ChoiceRejectsUndeclaredValueNamingTheChoices()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("owner/a/apps/forms/plan");
        await form.Define(new("Plan", [new("tier", "Tier", FormFieldKind.Choice, true, ["free", "pro"])]));
        var error = await Assert.ThrowsAsync<FormValidationException>(() => form.Submit(new([new("tier", "gold")], 1)));
        Assert.Contains("free", error.Message);
        Assert.Contains("pro", error.Message);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SecretFieldHoldsOnlyAReferenceAndRendersMasked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("owner/a/apps/forms/login");
        await form.Define(new("Login", [new("password", "Password", FormFieldKind.Secret)]));
        var secret = SecretReferences.For("owner", "login-password", "Password", isSet: true);
        await form.SetSecret("password", new FormSecret(secret.Reference, secret.Label, true));
        var state = await form.Read();
        var field = state.Fields.Single();
        Assert.True(field.SecretSet);
        Assert.Null(field.Value);
        // The handle is stored so a later grant can resolve it; only the reference, never a value.
        Assert.Equal(secret.Reference, field.Secret!.Reference);
        // Raw secret values are never accepted through a draft.
        await Assert.ThrowsAsync<ArgumentException>(() => form.SetDraft("password", "hunter2"));
        // A raw value submitted for a secret field is rejected too; only a FormSecret is allowed.
        var error = await Assert.ThrowsAsync<FormValidationException>(() => form.Submit(new([new("password", "hunter2")], state.Revision)));
        Assert.DoesNotContain("hunter2", error.Message, StringComparison.Ordinal);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ReferenceKindDeclaresTheFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("owner/a/apps/forms/ref");
        var state = await form.Define(new("Ref", [new("customer", "Customer", FormFieldKind.Reference)]));
        var field = state.Fields.Single();
        Assert.False(field.Supported);
        Assert.Equal(FormFieldKind.Reference, field.Kind);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SubmittedValuesSurviveReactivationInOneGrain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("owner/a/apps/forms/intake");
        var defined = await form.Define(Intake);
        await form.Submit(new([new("name", "Ada"), new("surname", "Lovelace"), new("birthDate", "1815-12-10")], defined.Revision));
        await brain.DeactivateAsync(form, ct);
        var reloaded = await form.Read();
        Assert.True(reloaded.Submitted);
        Assert.Equal(2, reloaded.Revision);
        Assert.Equal(3, reloaded.Fields.Count);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AnUnsupportedKindIsRejectedThroughTheFormContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var form = brain.Get<IForm>("f");
        await form.Define(new("Sign up", [new("age", "Age", FormFieldKind.Number, Required: true)]));
        var error = await Assert.ThrowsAsync<FormValidationException>(() => form.Submit(new([new("age", "forty")])));
        Assert.Equal(FormFieldKind.Number, error.Kind);
        Assert.Equal("forty", error.RejectedValue);
    }

    [Trait("host", "own")]
    [Fact]
    public void TheFormContractCarriesNoFrameworkType()
    {
        var types = typeof(IForm).Assembly.GetExportedTypes().SelectMany(t => t.GetProperties().Select(p => p.PropertyType).Concat(t.GetMethods().SelectMany(m => m.GetParameters().Select(p => p.ParameterType))));
        Assert.DoesNotContain(types, t => t.Namespace?.StartsWith("IntoChat.OS.Framework", StringComparison.Ordinal) == true
            || t.Namespace?.StartsWith("IntoChat.OS.Connections", StringComparison.Ordinal) == true || t.Namespace == "IntoChat.OS.Types");
    }
}
