using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Aspire.Hosting;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Framework.Platform.Integrations;
using DigitalBrain.Microsoft.GitHub;
using static DigitalBrain.Aspire.Testing.AspireCompositionTest;

namespace DigitalBrain.Modules.Microsoft.GitHub.Tests.Hosting;

public sealed class GitHubAppHostingFacts
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OAuthAndWebhookSecretsAreProjectedOnlyWhenTheirFeaturesAreConfigured(bool oauth, bool webhook)
    {
        var builder = CreateBuilder();
        var brain = builder.AddDigitalBrain("brain", options: new() { PersistentStorage = false })
            .WithHosting<GitHubModuleHosting>();
        new DigitalBrainModuleBuilder<GitHubModuleHosting>(brain).WithGitHubApp(options =>
            {
                options.AppId = 7;
                if (oauth) { options.ClientId = "client-id"; options.PublicOrigin = new Uri("https://brain.example"); }
                if (webhook) { options.PublicWebhookUrl = new Uri("https://brain.example/integrations/github/webhook"); }
            });
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.Contains(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("github", "PrivateKeyPem")), environment.Keys);
        Assert.Equal(oauth, environment.ContainsKey(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("github", "ClientSecret"))));
        Assert.Equal(oauth, environment.ContainsKey(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("github", "ClientId"))));
        // Every host gets a connections origin (its own endpoint by default); OAuth overrides it with the public origin.
        var origin = environment[DigitalBrainConfiguration.EnvironmentName(DigitalBrainConfiguration.ConnectionsPublicOriginKey)];
        Assert.Equal(oauth, Equals(origin, "https://brain.example/"));
        Assert.Equal(webhook, environment.ContainsKey(DigitalBrainConfiguration.EnvironmentName(IntegrationSchema.Key("github", "WebhookSecret"))));
        Assert.Equal(oauth, builder.Resources.OfType<ParameterResource>().Any(resource => resource.Name == "brain-github-app-client-secret"));
        Assert.Equal(1 + (oauth ? 1 : 0) + (webhook ? 1 : 0), builder.Resources.OfType<ParameterResource>().Count(resource => resource.Name.StartsWith("brain-github-app-", StringComparison.Ordinal)));
    }
}
