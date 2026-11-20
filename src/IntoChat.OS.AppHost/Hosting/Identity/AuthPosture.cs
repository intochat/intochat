using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Aspire.Hosting;

// The silo refuses to start without a declared IntoChatOS:Auth:Posture. A local run defaults
// to Open; publish carries no default. Bootstrap passwords remain secret parameter references.
internal static class AuthPosture
{
    private const string Key = AuthKeys.Posture;
    private const string PasswordParameterName = "intochat-os-bootstrap-password";

    public static IntoChatOSModuleProjection? Provision(IDistributedApplicationBuilder builder)
    {
        var posture = builder.Configuration[Key] ?? (builder.ExecutionContext.IsRunMode ? "Open" : null);
        var username = builder.Configuration[AuthKeys.Username];
        var password = builder.Configuration[AuthKeys.Password];
        if (string.IsNullOrEmpty(username) != string.IsNullOrEmpty(password))
        { throw new InvalidOperationException("A Basic bootstrap credential needs both IntoChatOS:Auth:Username and IntoChatOS:Auth:Password, or neither."); }
        if (posture is null) { return null; }
        ParameterResource? parameter = null;
        if (!string.IsNullOrEmpty(password))
        {
            parameter = builder.Resources.OfType<ParameterResource>().SingleOrDefault(resource => resource.Name == PasswordParameterName);
            if (parameter is not null && !parameter.Secret)
            { throw new InvalidOperationException("The bootstrap password parameter must be secret."); }
            parameter ??= builder.AddParameter(PasswordParameterName, password, publishValueAsDefault: false, secret: true).Resource;
        }
        return new Projection(posture, username, parameter, builder.ExecutionContext.IsRunMode);
    }

    private sealed class Projection(string posture, string? username, ParameterResource? password, bool runMode) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            builder.WithEnvironment(ModuleConfiguration.EnvironmentName(Key), posture);
            // The server has no Aspire execution context. An Aspire run starts its processes on this host, so they get the local
            // topology, where Open posture may listen on loopback only and IntoChatOS:Bind widens binding (BindPolicy, D-21).
            // A run on Azurite declares Topology=cloud itself (the orleans module projection); that one is kept.
            if (runMode)
            {
                builder.WithEnvironment(context => context.EnvironmentVariables.TryAdd(
                    ModuleConfiguration.EnvironmentName(IntoChatOSNames.TopologyKey), IntoChatOSNames.TopologyLocal));
            }
            if (password is not null)
            {
                builder.WithEnvironment(ModuleConfiguration.EnvironmentName(AuthKeys.Username), username);
                builder.WithEnvironment(context => context.EnvironmentVariables[ModuleConfiguration.EnvironmentName(AuthKeys.Password)] = password);
            }
        }
    }
}
