using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Platform.Integrations;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

// One per brain. Each secret field a module declares becomes a secret Aspire parameter and an environment
// variable under the integration key, so its value never appears as plain text in a resource annotation.
// A field with no deployment value is not seeded: the integration stays unconfigured rather than the host refusing to start.
internal sealed class ModuleSecretProjection : IntoChatOSModuleProjection
{
    private readonly List<(string Variable, IResourceBuilder<ParameterResource> Parameter)> _secrets = [];

    public static void Project(IntoChatOSBuilder brain, Type module)
    {
        var schemas = Declared(module, "Integration").Concat(Declared(module, "Integrations")).ToArray();
        if (schemas.Length == 0) { return; }

        var state = brain.GetOrAddState(_ => new ModuleSecretProjection(), out var added);
        if (added) { brain.AddProjection(state); }
        var configuration = brain.ApplicationBuilder.Configuration;
        foreach (var schema in schemas)
        {
            foreach (var field in schema.SecretFields.Concat(schema.OptionalSecretFields))
            {
                var name = $"{schema.Id}-{field}".ToLowerInvariant();
                var key = schema.Key(field);
                if (string.IsNullOrEmpty(HostConfiguration.Parameter(configuration, name) ?? configuration[key])) { continue; }

                var parameter = brain.ApplicationBuilder.AddParameter(brain.ResourceName(name),
                    () => HostConfiguration.Parameter(configuration, name) ?? configuration[key] ?? "", secret: true);
                state._secrets.Add((IntoChatOSConfiguration.EnvironmentName(key), parameter));
            }
        }
    }

    public override void Apply<TResource>(IResourceBuilder<TResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var (variable, parameter) in _secrets) { builder.WithEnvironment(variable, parameter); }
    }

    private static IEnumerable<IntegrationSchema> Declared(Type module, string member)
    {
        const BindingFlags Static = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var value = module.GetProperty(member, Static)?.GetValue(null) ?? module.GetField(member, Static)?.GetValue(null);
        return value switch
        {
            IntegrationSchema single => [single],
            IEnumerable<IntegrationSchema> many => many,
            _ => [],
        };
    }
}
