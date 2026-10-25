using System.Text.Json;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Contracts;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Framework.Platform.Secrets;
using IntoChat.OS.Authority.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntoChat.OS.Authority.Tests.Integration.Integrations;

public sealed class FakeGoogleModule() : Module(new ModuleId(nameof(FakeGoogleModule)))
{
    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("google", "Google")
        .RequiresSecret("ClientId")
        .RequiresSecret("ClientSecret")
        .RequiresSetting("RedirectPath");

    public override void Configure(ISiloBuilder silo) { }
}

public sealed class FakeRivalGoogleModule() : Module(new ModuleId(nameof(FakeRivalGoogleModule)))
{
    public static IntegrationSchema Integration { get; } = IntegrationSchema.For("google", "Rival Google");

    public override void Configure(ISiloBuilder silo) { }
}

public sealed class FakeAiModule() : Module(new ModuleId(nameof(FakeAiModule)))
{
    public static IntegrationSchema[] Integrations { get; } =
    [
        IntegrationSchema.For("openai", "OpenAI").RequiresSecret("ApiKey"),
        IntegrationSchema.For("anthropic", "Anthropic").RequiresSecret("ApiKey"),
    ];

    public override void Configure(ISiloBuilder silo) { }
}

[Trait("host", "own")]
public sealed class RegistrationFacts
{
    private const string Canary = "canary-registration-9d41c7";
    private const string OtherCanary = "canary-operator-55be02";

    [Fact]
    public async Task RegistrationReactivationRetainsSecretsSettingsRevisionAndSdkWireIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "google");
        var saved = await registration.Configure(Values(("ClientId", Canary + "-id"), ("ClientSecret", Canary), ("RedirectPath", "/callback")));
        await brain.DeactivateAsync(registration, ct);
        var restored = await registration.Read();
        Assert.Equal(saved.Revision, restored.Revision);
        Assert.Equal(IntegrationStatus.Ready, restored.Status);
        Assert.Equal(saved.Settings, restored.Settings);
        Assert.Empty(restored.MissingFields);
        var released = await registration.Release(Caller(CallerKind.Platform, TrustedEdge.Platform));
        Assert.Equal(Canary, released.Values["ClientSecret"]);
    }

    [Fact]
    public async Task ConfiguringAllFieldsMakesTheRegistrationReady()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "google");
        var snapshot = await registration.Configure(Values(("ClientId", Canary + "-id"), ("ClientSecret", Canary), ("RedirectPath", "/callback")));

        Assert.Equal(IntegrationStatus.Ready, snapshot.Status);
        Assert.Empty(snapshot.MissingFields);
        Assert.Equal("google", snapshot.IntegrationId);

        Assert.Equal(
            ["IntegrationId", "MissingFields", "Revision", "Settings", "Status"],
            typeof(IntegrationSnapshot).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(await registration.Read()), StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, string> { ["RedirectPath"] = "/callback" }, snapshot.Settings);
        Assert.Equal(snapshot.Settings, (await registration.Read()).Settings);
    }

    [Fact]
    public async Task SeedingAfterAnOperatorClearedEveryFieldIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var google = Registration(brain, "google");
        await google.Configure(Values(("ClientId", Canary)));
        var cleared = await google.Clear("ClientId");

        var seeded = await google.Seed(Values(("ClientId", OtherCanary)));

        Assert.Equal(IntegrationStatus.Unconfigured, seeded.Status);
        Assert.Equal(cleared.Revision, seeded.Revision);
        Assert.Equal(IntegrationStatus.Unconfigured, (await google.Read()).Status);
    }

    [Fact]
    public async Task APartialConfigurationReportsMissingFieldNamesOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "google");

        var snapshot = await registration.Configure(Values(("ClientId", Canary)));

        Assert.Equal(IntegrationStatus.Partial, snapshot.Status);
        Assert.Equal(["ClientSecret", "RedirectPath"], snapshot.MissingFields);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownFieldIsRejectedWithoutEchoingTheSubmittedValueOrStoringAnything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "google");

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => registration.Configure(Values(("ClientId", "fine"), ("NoSuchField", Canary))));

        Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("NoSuchField", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(IntegrationStatus.Unconfigured, (await registration.Read()).Status);
    }

    [Fact]
    public async Task ARegistrationForAnUndeclaredIntegrationRefusesEveryCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Registration(brain, "nothing").Read());
    }

    [Fact]
    public async Task ReleaseReturnsValuesOnlyForATrustedCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "openai");
        await registration.Configure(Values(("ApiKey", Canary)));

        foreach (var refused in new[]
        {
            Caller(CallerKind.User, TrustedEdge.AuthenticatedHttp),
            Caller(CallerKind.Assistant, TrustedEdge.AuthenticatedHttp),
            Caller(CallerKind.App, TrustedEdge.AppProxy),
            Caller(CallerKind.Scheduler, TrustedEdge.Scheduler),
            Caller(CallerKind.Platform, TrustedEdge.AuthenticatedHttp),
            Caller(CallerKind.Platform, TrustedEdge.Platform) with { PrincipalId = "" },
        })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Release(refused));
            Assert.DoesNotContain(Canary, error.ToString(), StringComparison.Ordinal);
        }

        var released = await registration.Release(Caller(CallerKind.Platform, TrustedEdge.Platform));
        Assert.Equal(Canary, released.Values["ApiKey"]);
        Assert.DoesNotContain(Canary, released.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseRefusesAnIncompleteRegistrationEvenForATrustedCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "google");
        await registration.Configure(Values(("ClientId", Canary)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registration.Release(Caller(CallerKind.Platform, TrustedEdge.Platform)));
    }

    [Fact]
    public async Task ReseedingKeepsAPersonValueAndDoesNotRestoreAClearedField()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = new Dictionary<string, string?>
        {
            [FakeGoogleModule.Integration.Key("ClientId")] = Canary + "-id",
            [FakeGoogleModule.Integration.Key("ClientSecret")] = Canary,
            [FakeGoogleModule.Integration.Key("RedirectPath")] = "/callback",
        };
        await using var brain = await StartAsync(ct, seeded);
        var google = Registration(brain, "google");
        Assert.Equal(IntegrationStatus.Ready, (await google.Read()).Status);
        Assert.Equal(IntegrationStatus.Unconfigured, (await Registration(brain, "openai").Read()).Status);

        await google.Clear("ClientId");
        await google.Configure(Values(("ClientSecret", OtherCanary)));
        var seeder = new IntegrationSeeder(
            new ConfigurationBuilder().AddInMemoryCollection(seeded).Build(),
            brain.Services.GetRequiredService<IReadOnlyList<IntegrationSchema>>(),
            brain.Platform,
            NullLogger<IntegrationSeeder>.Instance);
        await seeder.SeedAsync(ct);

        var after = await google.Read();
        Assert.Equal(IntegrationStatus.Partial, after.Status);
        Assert.Equal(["ClientId"], after.MissingFields);
        await google.Configure(Values(("ClientId", "restored")));
        var released = await google.Release(Caller(CallerKind.Platform, TrustedEdge.Platform));
        Assert.Equal(OtherCanary, released.Values["ClientSecret"]);
    }

    [Fact]
    public async Task ADeploymentEndpointThatMovedIsReconciledOnBoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct, new Dictionary<string, string?>
        {
            [FakeGoogleModule.Integration.Key("RedirectPath")] = "/callback",
        });
        var google = Registration(brain, "google");
        Assert.Equal("/callback", (await google.Read()).Settings["RedirectPath"]);

        await SeedAsync(brain, new Dictionary<string, string?>
        {
            [FakeGoogleModule.Integration.Key("RedirectPath")] = "/moved",
        }, ct);

        Assert.Equal("/moved", (await google.Read()).Settings["RedirectPath"]);
    }

    [Fact]
    public async Task APersonSetValueIsNotOverwrittenBySeeding()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct, new Dictionary<string, string?>
        {
            [FakeGoogleModule.Integration.Key("RedirectPath")] = "/callback",
        });
        var google = Registration(brain, "google");
        await google.Configure(Values(("RedirectPath", "/person")));

        await SeedAsync(brain, new Dictionary<string, string?>
        {
            [FakeGoogleModule.Integration.Key("RedirectPath")] = "/deployment",
        }, ct);

        Assert.Equal("/person", (await google.Read()).Settings["RedirectPath"]);
    }

    [Fact]
    public async Task AClearedFieldIsNotReseeded()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = new Dictionary<string, string?>
        {
            [FakeGoogleModule.Integration.Key("RedirectPath")] = "/callback",
        };
        await using var brain = await StartAsync(ct, seeded);
        var google = Registration(brain, "google");
        await google.Clear("RedirectPath");

        await SeedAsync(brain, seeded, ct);

        var after = await google.Read();
        Assert.Empty(after.Settings);
        Assert.Equal(IntegrationStatus.Unconfigured, after.Status);
    }

    [Fact]
    public async Task ClearingAFieldReturnsToPartialAndReleaseNoLongerHasIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "google");
        await registration.Configure(Values(("ClientId", Canary + "-id"), ("ClientSecret", Canary), ("RedirectPath", "/callback")));
        var platform = Caller(CallerKind.Platform, TrustedEdge.Platform);
        Assert.Equal(Canary, (await registration.Release(platform)).Values["ClientSecret"]);

        var snapshot = await registration.Clear("ClientSecret");

        Assert.Equal(IntegrationStatus.Partial, snapshot.Status);
        Assert.Equal(["ClientSecret"], snapshot.MissingFields);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Release(platform));
        await registration.Configure(Values(("ClientSecret", OtherCanary)));
        var released = await registration.Release(platform);
        Assert.Equal(OtherCanary, released.Values["ClientSecret"]);
        Assert.Equal(Canary + "-id", released.Values["ClientId"]);
        Assert.Equal("/callback", released.Values["RedirectPath"]);
    }

    [Fact]
    public async Task ClearingAPartiallyConfiguredRegistrationReportsPartial()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "google");
        await registration.Configure(Values(("ClientId", "a"), ("ClientSecret", "b"), ("RedirectPath", "/c")));

        var snapshot = await registration.Clear("ClientSecret");

        Assert.Equal(IntegrationStatus.Partial, snapshot.Status);
        Assert.Equal(["ClientSecret"], snapshot.MissingFields);
    }

    [Fact]
    public async Task TheCatalogReportsEveryDefinitionWithItsStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        await Registration(brain, "google").Configure(Values(("ClientId", Canary)));
        await Registration(brain, "openai").Configure(Values(("ApiKey", Canary)));

        var entries = await IntegrationCatalog.ListAsync(
            brain.Services.GetRequiredService<IReadOnlyList<IntegrationSchema>>(), brain.Platform, ct);

        // The host always composes the orleans module, so its integration is catalogued too.
        Assert.Equal(["anthropic", "google", "openai", "orleans"], entries.Select(entry => entry.Id).Order().ToArray());
        Assert.Equal("Partial", entries.Single(entry => entry.Id == "google").Status);
        Assert.Equal(["ClientSecret", "RedirectPath"], entries.Single(entry => entry.Id == "google").MissingFields);
        Assert.Equal("Ready", entries.Single(entry => entry.Id == "openai").Status);
        Assert.Equal("Unconfigured", entries.Single(entry => entry.Id == "anthropic").Status);
        Assert.Equal("Anthropic", entries.Single(entry => entry.Id == "anthropic").DisplayName);

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            ["displayName", "id", "missingFields", "status"],
            document.RootElement[0].EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.DoesNotContain(Canary, json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEndpointsAreRoutedWithTheCatalogOutsideAnyBrainScope()
    {
        var app = (IEndpointRouteBuilder)WebApplication.CreateBuilder().Build();
        app.MapIntegrations();

        var routes = app.DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(endpoint => (
                Method: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Single(),
                Pattern: endpoint.RoutePattern.RawText!.TrimEnd('/')))
            .ToArray();

        Assert.Contains(("GET", "/integrations"), routes);
        Assert.Contains(("POST", "/integrations/{id}/registration"), routes);
        Assert.Contains(("DELETE", "/integrations/{id}/registration/{field}"), routes);
        Assert.All(routes, route => Assert.DoesNotContain("brainId", route.Pattern, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyTheDeploymentOperatorMayWriteARegistration()
    {
        var basic = new IntoChat.OS.Connections.Identity.Configuration.AuthOptions
        { Posture = IntoChat.OS.Connections.Identity.Configuration.IdentityPosture.Secured, Username = "ops", Password = "a-long-operator-password" };
        var open = new IntoChat.OS.Connections.Identity.Configuration.AuthOptions
        { Posture = IntoChat.OS.Connections.Identity.Configuration.IdentityPosture.Open };

        Assert.True(IntegrationOperatorGate.Allows(Http("ops"), basic));
        Assert.False(IntegrationOperatorGate.Allows(Http("someone-else"), basic));
        Assert.False(IntegrationOperatorGate.Allows(Http("owner"), basic));
        Assert.True(IntegrationOperatorGate.Allows(Http("owner"), open));
        Assert.False(IntegrationOperatorGate.Allows(Http("cookie-owner-of-another-account"), open));
        Assert.False(IntegrationOperatorGate.Allows(Http("ops") with { Kind = CallerKind.App, StampedBy = TrustedEdge.AppProxy }, basic));
        Assert.False(IntegrationOperatorGate.Allows(null, open));
    }

    [Fact]
    public async Task RegistrationWritesRefuseAnAppStampedAmbientCallerEvenWithAForgedPlatformParameter()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "openai");
        await registration.Configure(Values(("ApiKey", Canary)));
        using (brain.Owner.AsApp("script"))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Configure(Values(("ApiKey", OtherCanary))));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Clear("ApiKey"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Seed(Values(("ApiKey", OtherCanary))));
        }

        var released = await registration.Release(Caller(CallerKind.Platform, TrustedEdge.Platform));
        Assert.Equal(Canary, released.Values["ApiKey"]);
    }

    [Fact]
    public async Task ReleaseSucceedsUnderAnAppStampedAmbientContextWhenTheParameterIsProperPlatform()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "openai");
        await registration.Configure(Values(("ApiKey", Canary)));

        using (brain.Owner.AsApp("script"))
        {
            var released = await registration.Release(Caller(CallerKind.Platform, TrustedEdge.Platform));
            Assert.Equal(Canary, released.Values["ApiKey"]);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => registration.Release(Caller(CallerKind.App, TrustedEdge.AppProxy)));
        }
    }

    [Fact]
    public async Task SettingValuesAreVisibleInTheSnapshotAndClearingRemovesThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await StartAsync(ct);
        var registration = Registration(brain, "google");

        var configured = await registration.Configure(Values(("ClientId", Canary), ("RedirectPath", "/here")));

        Assert.Equal(new Dictionary<string, string> { ["RedirectPath"] = "/here" }, configured.Settings);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(configured), StringComparison.Ordinal);
        Assert.Empty((await registration.Clear("RedirectPath")).Settings);
    }

    // The one Framework platform contract a brain member calls directly is the connection's user face;
    // the vault, registrations, indexes and the OAuth inbound are kernel grains, not neurons.
    private static readonly Type UserFaceOfAConnection = typeof(IntoChat.OS.Framework.Platform.Connections.IConnection);

    [Fact]
    public void NoPlatformContractIsANeuronButTheConnectionsUserFace()
    {
        Assert.False(typeof(INeuron).IsAssignableFrom(typeof(ISecrets)));
        Assert.False(typeof(INeuron).IsAssignableFrom(typeof(IIntegration)));
        Assert.DoesNotContain(typeof(IntegrationSchema).Assembly.GetExportedTypes(),
            type => type.IsInterface && typeof(INeuron).IsAssignableFrom(type) && type.Namespace!.Contains(".Platform.", StringComparison.Ordinal) && type != UserFaceOfAConnection);
        Assert.DoesNotContain(typeof(IntegrationDiscovery).Assembly.GetTypes(), type => type.IsInterface && typeof(INeuron).IsAssignableFrom(type));
    }

    [Fact]
    public void TheRegistrationsPlatformCallerCannotBeAnyAccount()
    {
        Assert.DoesNotMatch("^[a-z0-9][a-z0-9-]*$", IntegrationNeuron.PlatformCaller.AccountId);
        Assert.DoesNotMatch("^[a-z0-9][a-z0-9-]*$", IntegrationNeuron.PlatformCaller.BrainId);
    }

    private static CallerContext Http(string principal) => new()
    {
        PrincipalId = principal,
        AccountId = principal,
        BrainId = principal,
        Kind = CallerKind.User,
        StampedBy = TrustedEdge.AuthenticatedHttp,
    };

    [Fact]
    public void DefinitionsAreDiscoveredFromModuleTypesAndZeroDefinitionsIsFine()
    {
        Assert.Empty(IntegrationDiscovery.Collect([typeof(IntoChat.OS.Authority.PlatformHosting)]));

        var found = IntegrationDiscovery.Collect([typeof(FakeGoogleModule), typeof(FakeAiModule), typeof(IntoChat.OS.Authority.PlatformHosting)]);

        Assert.Equal(["anthropic", "google", "openai"], found.Select(definition => definition.Id).Order().ToArray());
        Assert.Throws<InvalidOperationException>(
            () => IntegrationDiscovery.Collect([typeof(FakeGoogleModule), typeof(FakeRivalGoogleModule)]));
    }

    [Fact]
    public void ADefinitionRejectsUnsafeOrDuplicateFieldNames()
    {
        var definition = IntegrationSchema.For("x", "X").RequiresSecret("Key");
        Assert.Throws<ArgumentException>(() => definition.RequiresSetting("Key"));
        Assert.Throws<ArgumentException>(() => definition.RequiresSecret("has space"));
        Assert.Throws<ArgumentException>(() => definition.RequiresSecret("a:b"));
        Assert.Throws<ArgumentException>(() => IntegrationSchema.For("Bad:Id", "X"));
    }

    // The deployment's integration values for google, seeded at boot.
    private static Task<ITestBrain> StartAsync(CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? google = null)
    {
        var builder = TestBrain.Create().WithModule<FakeGoogleModule>().WithModule<FakeAiModule>();
        foreach (var (key, value) in google ?? new Dictionary<string, string?>())
        { builder = builder.WithIntegration(FakeGoogleModule.Integration, key[(key.LastIndexOf(':') + 1)..], value!); }
        return builder.StartAsync(cancellationToken);
    }

    private static IIntegration Registration(ITestBrain brain, string id) => brain.Platform.Integration(id);

    private static Task SeedAsync(ITestBrain brain, IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken)
        => new IntegrationSeeder(
            new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
            brain.Services.GetRequiredService<IReadOnlyList<IntegrationSchema>>(),
            brain.Platform,
            NullLogger<IntegrationSeeder>.Instance).SeedAsync(cancellationToken);

    private static ConfigureIntegration Values(params (string Field, string Value)[] values)
        => new() { Values = values.ToDictionary(pair => pair.Field, pair => pair.Value) };

    private static CallerContext Caller(CallerKind kind, TrustedEdge edge) => new()
    {
        PrincipalId = "someone",
        AccountId = "someone",
        BrainId = "someone",
        Kind = kind,
        StampedBy = edge,
        AppId = "integrations-test",
    };
}
