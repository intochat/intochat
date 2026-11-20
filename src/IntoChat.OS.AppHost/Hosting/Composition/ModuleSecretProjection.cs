using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Modules;
using DigitalBrain.Modules.Configuration;
using Microsoft.Extensions.Configuration;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Aspire.Hosting;

// One per brain. Each secret field a module declares (via IntegrationSchema or ModuleManifest.Secrets) becomes a secret
// Aspire parameter and an environment variable, so its value never appears as plain text in a resource annotation.
// A field with no deployment value is not seeded: the integration stays unconfigured rather than the host refusing to start.
internal sealed class ModuleSecretProjection : IntoChatOSModuleProjection
{
    private readonly List<(string Variable, IResourceBuilder<ParameterResource> Parameter)> _secrets = [];
    private readonly HashSet<string> _variables = new(StringComparer.OrdinalIgnoreCase);

    public static void Project(IntoChatOSBuilder brain, Type module)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentNullException.ThrowIfNull(module);

        var schemas = OsModule.ReadSchemas(module);
        var manifest = module.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DigitalBrain.Modules.ModuleManifest;
        var moduleId = module.GetCustomAttribute<DigitalBrain.Modules.ModuleAttribute>()?.Id;
        if (schemas.Count == 0 && (manifest is null || manifest.Secrets.Count == 0 || string.IsNullOrEmpty(moduleId))) { return; }

        var state = brain.GetOrAddState(_ => new ModuleSecretProjection(), out var added);
        if (added) { brain.AddProjection(state); }
        var configuration = brain.ApplicationBuilder.Configuration;
        foreach (var schema in schemas)
        {
            foreach (var field in schema.SecretFields.Concat(schema.OptionalSecretFields))
            {
                state.AddSecret(brain, configuration, $"{schema.Id}-{field}".ToLowerInvariant(), schema.Key(field));
            }
        }
        if (manifest is not null && !string.IsNullOrEmpty(moduleId))
        {
            foreach (var secret in manifest.Secrets)
            {
                var name = $"{moduleId}-{secret.Name}".ToLowerInvariant();
                var key = ModuleConfiguration.ModuleSection(moduleId) + ":Secrets:" + secret.Name;
                state.AddSecret(brain, configuration, name, key);
            }
        }
    }

    private void AddSecret(IntoChatOSBuilder brain, IConfiguration configuration, string parameterName, string configurationKey)
    {
        var variable = ModuleConfiguration.EnvironmentName(configurationKey);
        if (!_variables.Add(variable)) { return; }
        if (string.IsNullOrEmpty(HostConfiguration.Parameter(configuration, parameterName) ?? configuration[configurationKey])) { return; }

        var parameter = brain.ApplicationBuilder.AddParameter(brain.ResourceName(parameterName),
            () => HostConfiguration.Parameter(configuration, parameterName) ?? configuration[configurationKey] ?? "", secret: true);
        _secrets.Add((variable, parameter));
    }

    public override void Apply<TResource>(IResourceBuilder<TResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var (variable, parameter) in _secrets) { builder.WithEnvironment(variable, parameter); }
    }
}
