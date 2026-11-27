using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace IntoChat.OS.AppHost.Hosting.Modules.Secrets;

/// <summary>
/// Aspire AppHost extensions for wiring Bitwarden Secrets Manager configuration into OS kernel resources.
/// </summary>
public static class BitwardenSecretsHostingExtensions
{
    public static IResourceBuilder<T> WithBitwardenSecretStore<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<ParameterResource> accessToken,
        string organizationId,
        string projectId,
        string deploymentId,
        Uri? apiUrl = null,
        Uri? identityUrl = null) where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deploymentId);

        builder
            .WithEnvironment("IntoChatOS__Secrets__Backend", "Bitwarden")
            .WithEnvironment("IntoChatOS__Secrets__Bitwarden__AccessToken", accessToken)
            .WithEnvironment("IntoChatOS__Secrets__Bitwarden__OrganizationId", organizationId)
            .WithEnvironment("IntoChatOS__Secrets__Bitwarden__ProjectId", projectId)
            .WithEnvironment("IntoChatOS__Secrets__Bitwarden__DeploymentId", deploymentId);

        if (apiUrl is not null)
        {
            builder.WithEnvironment("IntoChatOS__Secrets__Bitwarden__ApiUrl", apiUrl.AbsoluteUri);
        }

        if (identityUrl is not null)
        {
            builder.WithEnvironment("IntoChatOS__Secrets__Bitwarden__IdentityUrl", identityUrl.AbsoluteUri);
        }

        return builder;
    }
}
