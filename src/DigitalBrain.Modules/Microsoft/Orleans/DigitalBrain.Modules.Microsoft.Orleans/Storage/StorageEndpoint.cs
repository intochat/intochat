using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Microsoft.Orleans;

// One Orleans store is reached either by a shared-key connection string or by a service URI that
// authenticates with the deployment identity. The connection-string key also accepts an http(s)
// URI, so a deployment can switch to the identity without renaming its key.
internal sealed record StorageEndpoint(string? ConnectionString, Uri? ServiceUri)
{
    public const string UriSuffix = "Uri";

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
        : throw new InvalidOperationException($"The orleans integration {key} must be an absolute http(s) service URI.");

    private static bool IsHttpUri(string value, out Uri uri) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
