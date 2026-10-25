using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Testing.Aspire.Diagnostics;
using IntoChat.OS.Connections;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework.Tests.E2E;

// A secret must not appear in HTTP responses, traces, or logs. SecretsFacts inspects persisted state.
[Collection("ReferenceBrainFixture")]
public sealed class CanarySecretFacts(ReferenceBrainFixture host)
{
    private const string Canary = "canary-secret-7f3a91";

    [Fact(Timeout = 300_000)]
    public async Task ASeededVaultSecretNeverAppearsInPlaintext()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        using var operation = brain.Telemetry().BeginOperation(nameof(ASeededVaultSecretNeverAppearsInPlaintext));

        using var request = operation.Request(HttpMethod.Post,
            $"/secrets/{brain.Owner.BrainId}",
            JsonContent.Create(new { name = "me.apiKey", label = "API key", value = Canary }));
        using var seeded = await operation.SendAsync(brain.Owner.Http, request, ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, seeded.StatusCode);
        var seedBody = await seeded.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(Canary, seedBody, StringComparison.Ordinal);
        Assert.Contains("secret://", seedBody, StringComparison.Ordinal);

        await AssertPrivateAsync(operation, ct);
    }

    [Theory(Timeout = 300_000)]
    [InlineData("postgres")]
    public async Task AConnectionTokenNeverAppearsInPlaintext(string source)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        using var operation = brain.Telemetry().BeginOperation(nameof(AConnectionTokenNeverAppearsInPlaintext));

        // POST /brains/{brainId}/connections/{connectionId}/connect with a ConnectInput body.
        using var request = operation.Request(HttpMethod.Post,
            $"/brains/{brain.Owner.BrainId}/connections/conn-{source}/connect",
            JsonContent.Create(new { connectorId = source, authMethodId = "connection-string", value = Canary }));
        using var seeded = await operation.SendAsync(brain.Owner.Http, request, ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, seeded.StatusCode);
        var seedBody = await seeded.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(Canary, seedBody, StringComparison.Ordinal);

        using var listRequest = operation.Request(HttpMethod.Get, $"/brains/{brain.Owner.BrainId}/connections");
        using var listed = await operation.SendAsync(brain.Owner.Http, listRequest, ct);
        Assert.True(listed.IsSuccessStatusCode);
        var list = await listed.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(Canary, list, StringComparison.Ordinal);

        await AssertPrivateAsync(operation, ct);
    }

    [Fact(Timeout = 300_000)]
    public async Task AFormSecretTravelsOnlyAsAVaultReference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        using var operation = brain.Telemetry().BeginOperation(nameof(AFormSecretTravelsOnlyAsAVaultReference));

        // The brain id is the scope: a UI part named under it belongs to that brain.
        const string workspace = "form-canary";
        var form = brain.Get<IntoChat.OS.Modules.Flutter.Form.IForm>(workspace + "/apps/forms/intake");
        await form.Define(new("Intake", [new("password", "Password", IntoChat.OS.Modules.Flutter.Form.FormFieldKind.Secret)]));

        using var seedRequest = operation.Request(HttpMethod.Post,
            $"/secrets/{brain.Owner.BrainId}",
            JsonContent.Create(new { name = "forms.intake.password", label = "Password", value = Canary }));
        using var seeded = await operation.SendAsync(brain.Owner.Http, seedRequest, ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, seeded.StatusCode);
        var reference = (await seeded.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("reference").GetString();
        Assert.NotNull(reference);
        Assert.StartsWith("secret://", reference, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, reference, StringComparison.Ordinal);

        using var eventRequest = operation.Request(HttpMethod.Post,
            $"/brains/{workspace}/apps/event",
            JsonContent.Create(new { kind = "form", name = workspace + "/apps/forms/intake", action = "secret", field = "password", value = reference }));
        using var evented = await operation.SendAsync(brain.Owner.Http, eventRequest, ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, evented.StatusCode);

        var state = await form.Read();
        var field = state.Fields.Single();
        Assert.True(field.SecretSet);
        Assert.Null(field.Value);
        Assert.Equal(reference, field.Secret!.Reference);
        Assert.DoesNotContain(Canary, System.Text.Json.JsonSerializer.Serialize(state), StringComparison.Ordinal);

        await AssertPrivateAsync(operation, ct);
    }

    private static async Task AssertPrivateAsync(TelemetryOperation operation, CancellationToken ct)
    {
        var capture = await operation.CompleteAsync(ct);
        Assert.NotEmpty(capture.Spans);
        Assert.DoesNotContain(capture.PrivacySpans, span => span.Text.Contains(Canary, StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Logs, log => log.Text.Contains(Canary, StringComparison.Ordinal));
    }
}
