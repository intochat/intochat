using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Connections.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Integrations;
using IntoChat.OS.Modules.Google.Gmail;
using IntoChat.OS.Modules.Salesforce;
using IntoChat.OS.Modules.Salesforce.Aspire.Hosting;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ModuleHostingProjectionFacts
{
    private sealed class SampleModule() : Module(new ModuleId("projection-sample"))
    {
        public static IntegrationSchema Integration { get; } = IntegrationSchema.For("projection-sample", "Projection sample")
            .RequiresSecret("ApiKey").RequiresSecret("ClientSecret").OffersSecret("Token").RequiresSetting("Region");

    }

    private sealed class SampleHosting : ModuleHosting<SampleModule>;

    private sealed class OptOutHosting : ModuleHosting<SampleModule>
    {
        public override void Configure(IntoChatOSBuilder brain) { }
    }

    private static (IDistributedApplicationBuilder Builder, IResourceBuilder<ExecutableResource> Host) Compose<THosting>(
        IntegrationSchema schema, IReadOnlyDictionary<string, string> values, bool hosted = true)
        where THosting : IIntoChatOSModuleHosting, new()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        foreach (var pair in values) { builder.Configuration[schema.Key(pair.Key)] = pair.Value; }
        var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions());
        if (hosted) { brain.WithHosting<THosting>(); }
        return (builder, builder.AddExecutable("host", "unused", ".").WithReference(brain));
    }

    private static async Task<Dictionary<string, object>> EnvironmentOf(IDistributedApplicationBuilder builder, IResource resource)
    {
        var values = new Dictionary<string, object>();
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, values, TestContext.Current.CancellationToken);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await annotation.Callback(context); }
        return values;
    }

    [Fact]
    public async Task DeclaredSecretsAreProjectedAsSecretParametersUnderTheirIntegrationEnvironmentNames()
    {
        var schema = SampleModule.Integration;
        var (builder, host) = Compose<SampleHosting>(schema, new Dictionary<string, string> { ["ApiKey"] = "key-value", ["ClientSecret"] = "secret-value" });

        var parameters = builder.Resources.OfType<ParameterResource>().Where(p => p.Name.Contains("projection-sample", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, parameters.Length);
        Assert.All(parameters, p => Assert.True(p.Secret));

        var environment = await EnvironmentOf(builder, host.Resource);
        foreach (var field in new[] { "ApiKey", "ClientSecret" })
        {
            var variable = IntoChatOSConfiguration.EnvironmentName(schema.Key(field));
            Assert.True(environment[variable] is ParameterResource { Secret: true }, $"{variable} must carry a secret parameter, not text");
        }

        // The optional secret has no value, so nothing is seeded for it.
        Assert.DoesNotContain(IntoChatOSConfiguration.EnvironmentName(schema.Key("Token")), environment.Keys);
    }

    [Fact]
    public async Task SecretValuesNeverAppearAsPlainTextInEnvironmentOrNonSecretParameters()
    {
        var schema = SampleModule.Integration;
        var (builder, host) = Compose<SampleHosting>(schema, new Dictionary<string, string> { ["ApiKey"] = "key-value", ["ClientSecret"] = "secret-value" });
        var environment = await EnvironmentOf(builder, host.Resource);
        var plain = environment.Values.OfType<string>().ToArray();
        Assert.DoesNotContain("key-value", plain);
        Assert.DoesNotContain("secret-value", plain);
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), p => !p.Secret && p.Name.Contains("projection-sample", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostingThatOverridesConfigureWithoutBaseProjectsNothing()
    {
        var (builder, _) = Compose<OptOutHosting>(SampleModule.Integration, new Dictionary<string, string> { ["ApiKey"] = "key-value" });
        Assert.DoesNotContain(builder.Resources.OfType<ParameterResource>(), p => p.Name.Contains("projection-sample", StringComparison.Ordinal));
    }

    // Before/after equivalence for the migrated modules: the environment names a silo host receives, and the value
    // each resolves to, are the same whether or not the module hosting is registered (the pre-migration hosting was
    // a no-op, so "not registered" is the pre-migration projection).
    [Fact]
    public async Task SalesforceHostingProjectsTheSameEnvironmentNamesAndValuesAsWithoutIt()
        => await AssertEquivalent<SalesforceModuleHosting>(SalesforceModule.Integration,
            new Dictionary<string, string> { ["ConsumerKey"] = "consumer-key", ["ConsumerSecret"] = "consumer-secret" });

    [Fact]
    public async Task GmailHostingProjectsTheSameEnvironmentNamesAndValuesAsWithoutIt()
    {
        await AssertEquivalent<IntoChat.OS.Modules.Google.Auth.GoogleAuthModuleHosting>(IntoChat.OS.Modules.Google.Auth.GoogleAuthModule.Integration,
            new Dictionary<string, string> { ["ClientId"] = "client-id", ["ClientSecret"] = "client-secret" });
        await AssertEquivalent<GmailModuleHosting>(GmailModule.Integration,
            new Dictionary<string, string> { ["Audience"] = "https://audience", ["ServiceAccountEmail"] = "sa@example.test" });
    }

    private static async Task AssertEquivalent<THosting>(IntegrationSchema schema, Dictionary<string, string> values)
        where THosting : IIntoChatOSModuleHosting, new()
    {
        var (beforeBuilder, beforeHost) = Compose<THosting>(schema, values, hosted: false);
        var (afterBuilder, afterHost) = Compose<THosting>(schema, values);
        var before = await EnvironmentOf(beforeBuilder, beforeHost.Resource);
        var after = await EnvironmentOf(afterBuilder, afterHost.Resource);

        var names = values.Keys.Select(field => IntoChatOSConfiguration.EnvironmentName(schema.Key(field))).ToArray();
        Assert.Equal(names.Order(StringComparer.Ordinal), before.Keys.Where(names.Contains).Order(StringComparer.Ordinal));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (field, expected) in values)
        {
            var variable = IntoChatOSConfiguration.EnvironmentName(schema.Key(field));
            Assert.Equal(expected, await ValueOf(before[variable]));
            Assert.Equal(expected, await ValueOf(after[variable]));
            Assert.Equal(schema.SecretFields.Contains(field, StringComparer.Ordinal), after[variable] is ParameterResource { Secret: true });
        }
    }

    private static async Task<string?> ValueOf(object value)
        => value is ParameterResource parameter ? await parameter.GetValueAsync(TestContext.Current.CancellationToken) : value as string;
}
