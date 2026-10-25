using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Platform.Integrations;
using IntoChat.OS.Microsoft.GitHub;
using static IntoChat.OS.Aspire.Testing.AspireCompositionTest;

namespace IntoChat.OS.Modules.Microsoft.GitHub.Tests.Hosting;

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
        var brain = builder.AddIntoChatOS("brain", options: new())
            .WithHosting<GitHubModuleHosting>();
        new IntoChatOSModuleBuilder<GitHubModuleHosting>(brain).WithGitHubApp(options =>
            {
                options.AppId = 7;
                if (oauth) { options.ClientId = "client-id"; options.PublicOrigin = new Uri("https://brain.example"); }
                if (webhook) { options.PublicWebhookUrl = new Uri("https://brain.example/integrations/github/webhook"); }
            });
        var consumer = builder.AddExecutable("consumer", "unused", ".").WithReference(brain);
        var environment = await EnvironmentOf(builder, consumer.Resource, TestContext.Current.CancellationToken);
        Assert.Contains(IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("github", "PrivateKeyPem")), environment.Keys);
        Assert.Equal(oauth, environment.ContainsKey(IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("github", "ClientSecret"))));
        Assert.Equal(oauth, environment.ContainsKey(IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("github", "ClientId"))));
        // Every host gets a connections origin (its own endpoint by default); OAuth overrides it with the public origin.
        var origin = environment[IntoChatOSConfiguration.EnvironmentName(IntoChatOSConfiguration.ConnectionsPublicOriginKey)];
        Assert.Equal(oauth, Equals(origin, "https://brain.example/"));
        Assert.Equal(webhook, environment.ContainsKey(IntoChatOSConfiguration.EnvironmentName(IntegrationSchema.Key("github", "WebhookSecret"))));
        Assert.Equal(oauth, builder.Resources.OfType<ParameterResource>().Any(resource => resource.Name == "brain-github-app-client-secret"));
        Assert.Equal(1 + (oauth ? 1 : 0) + (webhook ? 1 : 0), builder.Resources.OfType<ParameterResource>().Count(resource => resource.Name.StartsWith("brain-github-app-", StringComparison.Ordinal)));
    }
}
