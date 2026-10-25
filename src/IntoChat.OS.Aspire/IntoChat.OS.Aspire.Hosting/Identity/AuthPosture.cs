using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Connections.Identity.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

// The silo refuses to start without a declared IntoChatOS:Auth:Posture. A local run defaults
// to Open; publish carries no default. Bootstrap passwords remain secret parameter references.
internal static class AuthPosture
{
    private const string Key = AuthOptions.PostureKey;
    private const string PasswordParameterName = "intochat-os-bootstrap-password";

    public static IntoChatOSModuleProjection? Provision(IDistributedApplicationBuilder builder)
    {
        var posture = builder.Configuration[Key] ?? (builder.ExecutionContext.IsRunMode ? "Open" : null);
        var username = builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Username))];
        var password = builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Password))];
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
            builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(Key), posture);
            // The server has no Aspire execution context: this marker tells it the process was started by an Aspire run, where
            // Open posture may listen on loopback only (OpenPostureLoopback).
            if (runMode) { builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(IntoChatOSNames.HostingModeConfigurationKey), IntoChatOSNames.HostingModeRun); }
            if (password is not null)
            {
                builder.WithEnvironment(IntoChatOSConfiguration.EnvironmentName(AuthOptions.Key(nameof(AuthOptions.Username))), username);
                builder.WithEnvironment(context => context.EnvironmentVariables[IntoChatOSConfiguration.EnvironmentName(AuthOptions.Key(nameof(AuthOptions.Password)))] = password);
            }
        }
    }
}
