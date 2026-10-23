using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IntoChat.OS.Authority.Entries;
using IntoChat.OS.Authority.Tests.Integration.Integrations;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Platform.Connections;
using IntoChat.OS.Framework.Platform.Entries;
using IntoChat.OS.Framework.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace IntoChat.OS.Authority.Tests.Integration.Entries;

// One host for the class: FakeAiModule composes the openai and anthropic integrations, and the
// in-memory exporter keeps every log record the brain wrote.
public sealed class BrainEntriesFacts : IAsyncLifetime
{
    private const string Canary = "sk-canary-0123456789-ABCD";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ITestBrain _brain = null!;
    private InMemoryTelemetrySink _telemetry = null!;

    public async ValueTask InitializeAsync()
        => _brain = await TestBrain.Create().WithModule<FakeAiModule>().WithInMemoryTelemetry(out _telemetry).StartAsync(Ct);

    public async ValueTask DisposeAsync() => await _brain.DisposeAsync();

    [Fact]
    public async Task Variable_round_trips_value()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);

        using var put = await Put(owner, owner.BrainId, "REGION", "variable", "eu-west");
        var row = Assert.Single(await List(owner));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("REGION", row.GetProperty("name").GetString());
        Assert.Equal("variable", row.GetProperty("kind").GetString());
        Assert.True(row.GetProperty("isSet").GetBoolean());
        Assert.Equal("eu-west", row.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Secret_value_is_never_returned()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);

        using var put = await Put(owner, owner.BrainId, "provider:openai", "secret", Canary);
        using var get = await owner.Http.GetAsync($"/brains/{owner.BrainId}/entries", Ct);
        var body = await get.Content.ReadAsStringAsync(Ct);
        var sealedSecret = await Stored(owner, "provider:openai");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.DoesNotContain(Canary, body, StringComparison.Ordinal);
        Assert.DoesNotContain(sealedSecret.Ciphertext, body, StringComparison.Ordinal);
        Assert.DoesNotContain(sealedSecret.KeyId, body, StringComparison.Ordinal);
        var row = Assert.Single(JsonDocument.Parse(body).RootElement.EnumerateArray());
        Assert.Equal("secret", row.GetProperty("kind").GetString());
        Assert.True(row.GetProperty("isSet").GetBoolean());
        Assert.Equal("ABCD", row.GetProperty("tail").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData("short-k", null)]
    [InlineData("1234567", null)]
    [InlineData("12345678", "5678")]
    [InlineData("abcd🔑🔑🔑🔑", "🔑🔑🔑🔑")]
    [InlineData("abc🔑🔑🔑🔑", null)]
    public async Task Short_secret_shows_no_tail(string secret, string? tail)
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);

        using var put = await Put(owner, owner.BrainId, "provider:openai", "secret", secret);
        var row = Assert.Single(await List(owner));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(tail, row.GetProperty("tail").GetString());
    }

    [Fact]
    public async Task Secret_is_sealed_with_brain_key()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);

        using var put = await Put(owner, owner.BrainId, "SERVICE_TOKEN", "secret", Canary);
        var sealedSecret = await Stored(owner, "SERVICE_TOKEN");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.StartsWith(BrainSeal.Prefix, sealedSecret.KeyId, StringComparison.Ordinal);
        Assert.True(BrainSeal.IsBrainSealed(sealedSecret));
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(await State(owner)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_owner_gets_403_on_put_and_delete()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);
        using var member = await _brain.PersonAsync(AuthorityHost.Unique("member"), Ct);
        await _brain.Platform.Directory().ShareBrainAsync(owner.AccountId, owner.BrainId, member.PrincipalId, "Member", MemberRole.Member, Ct);
        using var selected = await member.Http.PostAsJsonAsync("/identity/session/brain", new { owner.AccountId, owner.BrainId }, Ct);
        selected.EnsureSuccessStatusCode();
        using var seeded = await Put(owner, owner.BrainId, "REGION", "variable", "eu-west");

        using var put = await Put(member, owner.BrainId, "REGION", "variable", "us-east");
        using var delete = await member.Http.DeleteAsync($"/brains/{owner.BrainId}/entries/REGION", Ct);
        using var list = await member.Http.GetAsync($"/brains/{owner.BrainId}/entries", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal("eu-west", Assert.Single(await List(owner)).GetProperty("value").GetString());
    }

    // A non-member (never shared the brain) and a user of another account (whose own brain the owner
    // belongs to, so a relationship exists but not on this brain) are both refused, and nothing changes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outsider_gets_403_on_get_put_and_delete(bool otherAccountSharesWithOwner)
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);
        using var outsider = await _brain.PersonAsync(AuthorityHost.Unique("outsider"), Ct);
        if (otherAccountSharesWithOwner)
        { await _brain.Platform.Directory().ShareBrainAsync(outsider.AccountId, outsider.BrainId, owner.PrincipalId, "Owner", MemberRole.Member, Ct); }
        using var seeded = await Put(owner, owner.BrainId, "REGION", "variable", "eu-west");
        using var seededKey = await Put(owner, owner.BrainId, "provider:openai", "secret", Canary);
        var before = await Entries(owner).Version();

        using var list = await outsider.Http.GetAsync($"/brains/{owner.BrainId}/entries", Ct);
        using var put = await Put(outsider, owner.BrainId, "REGION", "variable", "us-east");
        using var putKey = await Put(outsider, owner.BrainId, "provider:openai", "secret", "sk-outsider");
        using var delete = await outsider.Http.DeleteAsync($"/brains/{owner.BrainId}/entries/REGION", Ct);
        var listBody = await list.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.NoContent, seeded.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, putKey.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.DoesNotContain("eu-west", listBody, StringComparison.Ordinal);
        Assert.Equal(before, await Entries(owner).Version());
        var rows = await List(owner);
        Assert.Equal("eu-west", Assert.Single(rows, row => row.GetProperty("name").GetString() == "REGION").GetProperty("value").GetString());
        Assert.Contains(rows, row => row.GetProperty("name").GetString() == "provider:openai" && row.GetProperty("isSet").GetBoolean());
    }

    [Fact]
    public async Task Unknown_provider_name_is_400()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);

        using var put = await Put(owner, owner.BrainId, "provider:nobody", "secret", Canary);
        using var badName = await Put(owner, owner.BrainId, "9starts-with-digit", "variable", "x");

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.DoesNotContain(Canary, await put.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Empty(await List(owner));
    }

    [Fact]
    public async Task User_entry_cannot_use_provider_prefix_as_variable()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);

        using var put = await Put(owner, owner.BrainId, "provider:openai", "variable", "visible");

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Empty(await List(owner));
    }

    [Fact]
    public async Task Oversized_values_are_400()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);

        using var variable = await Put(owner, owner.BrainId, "BIG", "variable", new string('v', BrainEntries.MaxVariableBytes + 1));
        using var secret = await Put(owner, owner.BrainId, "BIG_SECRET", "secret", new string('s', BrainEntries.MaxSecretBytes + 1));

        Assert.Equal(HttpStatusCode.BadRequest, variable.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, secret.StatusCode);
    }

    [Fact]
    public async Task Remove_unsets()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);
        using var seeded = await Put(owner, owner.BrainId, "provider:anthropic", "secret", Canary);

        using var delete = await owner.Http.DeleteAsync($"/brains/{owner.BrainId}/entries/provider:anthropic", Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Empty(await List(owner));
        Assert.Null(await Entries(owner).Release(IntegrationPlatformCaller, "provider:anthropic"));
    }

    [Fact]
    public async Task Release_opens_only_for_platform_callers()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);
        using var seeded = await Put(owner, owner.BrainId, "provider:openai", "secret", Canary);
        var user = new CallerContext
        {
            PrincipalId = owner.PrincipalId,
            AccountId = owner.AccountId,
            BrainId = owner.BrainId,
            Kind = CallerKind.User,
            StampedBy = TrustedEdge.AuthenticatedHttp,
        };

        var released = await Entries(owner).Release(IntegrationPlatformCaller, "provider:openai");
        var refused = await Assert.ThrowsAsync<UntrustedCallerException>(() => Entries(owner).Release(user, "provider:openai"));

        Assert.Equal(Canary, released);
        Assert.DoesNotContain(Canary, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Value_absent_from_logs()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);

        using var secret = await Put(owner, owner.BrainId, "provider:openai", "secret", Canary);
        using var rejected = await Put(owner, owner.BrainId, "provider:openai", "variable", Canary);
        using var unknown = await Put(owner, owner.BrainId, "provider:nobody", "secret", Canary);
        _ = await List(owner);

        var records = _telemetry.LogRecords();
        Assert.Contains(records, record => (record.FormattedMessage ?? record.Body ?? "").Contains("provider:openai", StringComparison.Ordinal));
        Assert.All(records, record =>
        {
            var text = string.Join("|", record.FormattedMessage, record.Body, record.Exception,
                string.Join(",", record.Attributes?.Select(pair => $"{pair.Key}={pair.Value}") ?? []));
            Assert.DoesNotContain(Canary, text, StringComparison.Ordinal);
            Assert.DoesNotContain(Canary[^12..], text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Provider_keys_release_only_the_provider_entry()
    {
        using var owner = await _brain.PersonAsync(AuthorityHost.Unique("owner"), Ct);
        var scope = BrainScope.Create(owner.AccountId, owner.BrainId).Id;
        var keys = _brain.Services.GetRequiredService<IBrainProviderKeys>();
        using var provider = await Put(owner, owner.BrainId, "provider:openai", "secret", Canary);
        using var other = await Put(owner, owner.BrainId, "OTHER", "secret", "other-secret-value");

        Assert.Equal(Canary, await keys.Release(scope, "openai", Ct));
        Assert.Null(await keys.Release(scope, "anthropic", Ct));
    }

    [Theory]
    [InlineData("a/b", "openai")]
    [InlineData("", "openai")]
    [InlineData("personal", "OTHER")]
    [InlineData("personal", "nobody")]
    [InlineData("personal", "")]
    public async Task Provider_keys_reject_invalid_ids(string brain, string provider)
    {
        var keys = _brain.Services.GetRequiredService<IBrainProviderKeys>();
        await Assert.ThrowsAsync<ArgumentException>(() => keys.Release(brain, provider, Ct));
    }

    private static readonly CallerContext IntegrationPlatformCaller = IntoChat.OS.Authority.Integrations.IntegrationNeuron.PlatformCaller;

    private static Task<HttpResponseMessage> Put(Person person, string brainId, string name, string kind, string value)
        => person.Http.PutAsJsonAsync($"/brains/{brainId}/entries/{name}", new { kind, value }, Ct);

    private static async Task<JsonElement[]> List(Person person)
    {
        using var response = await person.Http.GetAsync($"/brains/{person.BrainId}/entries", Ct);
        response.EnsureSuccessStatusCode();
        return [.. JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.EnumerateArray()];
    }

    private IBrainEntries Entries(Person person)
        => _brain.Platform.Entries(BrainScope.Create(person.AccountId, person.BrainId).Id);

    private async Task<BrainEntriesState> State(Person person)
    {
        var storage = _brain.Services.GetRequiredKeyedService<IGrainStorage>(IntoChatOSNames.PlatformGrainStorage);
        var state = new GrainState<BrainEntriesState>();
        await storage.ReadStateAsync(BrainEntriesNeuron.StateName, Entries(person).GetGrainId(), state);
        return state.State!;
    }

    private async Task<SealedSecret> Stored(Person person, string name)
        => Assert.Single((await State(person)).Entries, entry => entry.Name == name).Sealed!;
}
