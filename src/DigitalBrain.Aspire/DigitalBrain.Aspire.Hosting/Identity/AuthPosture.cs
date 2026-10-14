using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Platform.Identity.Configuration;

namespace DigitalBrain.Aspire.Hosting;

// The silo refuses to start without a declared DigitalBrain:Auth:Posture. A local run defaults
// to Open; publish carries no default. Bootstrap passwords remain secret parameter references.
internal static class AuthPosture
{
    private const string Key = AuthOptions.PostureKey;
    private const string PasswordParameterName = "digitalbrain-bootstrap-password";

    public static DigitalBrainModuleProjection? Provision(IDistributedApplicationBuilder builder)
    {
        var posture = builder.Configuration[Key] ?? (builder.ExecutionContext.IsRunMode ? "Open" : null);
        var username = builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Username))];
        var password = builder.Configuration[AuthOptions.Key(nameof(AuthOptions.Password))];
        if (string.IsNullOrEmpty(username) != string.IsNullOrEmpty(password))
        { throw new InvalidOperationException("A Basic bootstrap credential needs both DigitalBrain:Auth:Username and DigitalBrain:Auth:Password, or neither."); }
        if (posture is null) { return null; }
        ParameterResource? parameter = null;
        if (!string.IsNullOrEmpty(password))
        {
            parameter = builder.Resources.OfType<ParameterResource>().SingleOrDefault(resource => resource.Name == PasswordParameterName);
            if (parameter is not null && !parameter.Secret)
            { throw new InvalidOperationException("The bootstrap password parameter must be secret."); }
            parameter ??= builder.AddParameter(PasswordParameterName, password, publishValueAsDefault: false, secret: true).Resource;
        }
        return new Projection(posture, username, parameter);
    }

    private sealed class Projection(string posture, string? username, ParameterResource? password) : DigitalBrainModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
        {
            builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(Key), posture);
            if (password is not null)
            {
                builder.WithEnvironment(DigitalBrainConfiguration.EnvironmentName(AuthOptions.Key(nameof(AuthOptions.Username))), username);
                builder.WithEnvironment(context => context.EnvironmentVariables[DigitalBrainConfiguration.EnvironmentName(AuthOptions.Key(nameof(AuthOptions.Password)))] = password);
            }
        }
    }
}
