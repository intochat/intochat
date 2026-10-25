using System.Reflection;
using IntoChat.OS.Sdk.Platform.Integrations;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Sdk.Configuration;

namespace IntoChat.OS.Connections.Configuration;

// The one place module code reads configuration that is not a module option. Module options bind through
// GetModuleOptions<T>; everything else the host owns (Aspire parameters, OTLP, integration values, the
// IntoChatOSNames host keys) comes through here, so a raw configuration read in module source is a defect.
public static class HostConfiguration
{
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private static readonly HashSet<string> HostKeys = typeof(IntoChatOSNames)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.EndsWith("ConfigurationKey", StringComparison.Ordinal))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // An Aspire AddParameter value: Parameters:{name}.
    public static string? Parameter(IConfiguration configuration, string name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return configuration[$"Parameters:{name}"];
    }

    // A deployment value of an integration. Only fields the schema declares can be read, so a secret that the
    // integration does not declare never flows through here.
    public static string? IntegrationValue(IConfiguration configuration, IntegrationSchema schema, string field)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (!schema.AllFields.Contains(field, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Integration '{schema.Id}' does not declare field '{field}'.", nameof(field));
        }

        return configuration[schema.Key(field)];
    }

    public static string? OtlpEndpoint(IConfiguration configuration) => Variable(configuration, OtlpEndpointVariable);

    public static string? ConnectionString(IConfiguration configuration, string name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return configuration.GetConnectionString(name);
    }

    // A host-level key spelled as an IntoChatOSNames *ConfigurationKey constant; any other key is refused.
    public static string? HostKey(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!HostKeys.Contains(key))
        {
            throw new ArgumentException($"'{key}' is not an IntoChatOSNames host key.", nameof(key));
        }

        return configuration[key];
    }

    // A process-level variable the platform or a dashboard sets (AZURE_CLIENT_ID, the dashboard OTLP URL).
    public static string? Variable(IConfiguration configuration, string name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("A variable name is not a configuration path.", nameof(name));
        }

        return configuration[name];
    }

    // Whether the deployment stated a module option explicitly, as opposed to the options record's default.
    public static bool IsModuleOptionSet(IConfiguration configuration, string moduleId, string option)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(option);
        return !string.IsNullOrWhiteSpace(configuration[$"{IntoChatOSConfiguration.ModuleOptionsSection(moduleId)}:{option}"]);
    }
}
