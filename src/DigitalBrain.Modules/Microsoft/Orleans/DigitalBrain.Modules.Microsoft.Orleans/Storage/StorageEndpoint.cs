using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Microsoft.Orleans;

// One Orleans store is reached either by a shared-key connection string or by a service URI that
// authenticates with the deployment identity. The connection-string key also accepts an https
// URI (http only for a loopback emulator), so a deployment can switch to the identity without renaming its key.
public sealed record StorageEndpoint(string? ConnectionString, Uri? ServiceUri)
{
    public const string UriSuffix = "Uri";
    public const string ManagedIdentityClientIdField = "ManagedIdentityClientId";
    public const string ClientIdVariable = "AZURE_CLIENT_ID";

    // A user-assigned identity needs its client id; the integration setting wins over AZURE_CLIENT_ID.
    public static DefaultAzureCredentialOptions CredentialOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var clientId = configuration[OrleansModule.Integration.Key(ManagedIdentityClientIdField)];
        if (string.IsNullOrWhiteSpace(clientId)) { clientId = configuration[ClientIdVariable] ?? Environment.GetEnvironmentVariable(ClientIdVariable); }
        return string.IsNullOrWhiteSpace(clientId) ? new() : new() { ManagedIdentityClientId = clientId.Trim() };
    }

    public static TokenCredential Credential(IConfiguration configuration) => new DefaultAzureCredential(CredentialOptions(configuration));

    public static StorageEndpoint? Read(IConfiguration configuration, string field) => Select(field,
        configuration[OrleansModule.Integration.Key(field)],
        configuration[OrleansModule.Integration.Key(field + UriSuffix)]);

    public static StorageEndpoint? Select(string field, string? connectionString, string? serviceUri)
    {
        var hasConnection = !string.IsNullOrWhiteSpace(connectionString);
        var hasUri = !string.IsNullOrWhiteSpace(serviceUri);
        if (hasConnection && hasUri)
        {
            throw new InvalidOperationException(
                $"The orleans integration sets both {field} and {field}{UriSuffix}; set one: a connection string or a service URI.");
        }
        if (hasUri) { return new(null, ParseUri(field + UriSuffix, serviceUri!)); }
        if (!hasConnection) { return null; }
        return IsHttpUri(connectionString!, out var uri) ? new(null, uri) : new(connectionString, null);
    }

    private static Uri ParseUri(string key, string value) => IsHttpUri(value, out var uri) ? uri
        : throw new InvalidOperationException($"The orleans integration {key} must be an absolute https service URI (http only for localhost).");

    private static bool IsHttpUri(string value, out Uri uri) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri!)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
