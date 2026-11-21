using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS;
using IntoChat.OS.Host.Configuration;

namespace IntoChat.OS.Aspire.Hosting;

// The master key protecting owner data keys: generated and persisted by Aspire in run mode,
// supplied as a deployment parameter in publish mode, and projected to silos only — clients
// never receive it. The platform refuses to start without it.
internal static class MasterKey
{
    internal const string ParameterName = "intochat-os-master-key";

    public static IntoChatOSModuleProjection Provision(IDistributedApplicationBuilder builder, string brainName)
    {
        var scopedName = $"{brainName}-{ParameterName}";
        if (builder.Configuration[$"Parameters:{ParameterName}"] is not null
            && builder.Configuration[$"Parameters:{scopedName}"] is null)
        {
            throw new InvalidOperationException($"Copy the existing master key to Parameters:{scopedName} before upgrading. Existing encrypted data must keep its original key.");
        }
        var parameter = builder.ExecutionContext.IsRunMode
            ? builder.AddParameter(scopedName, new GenerateParameterDefault { MinLength = 32 }, secret: true, persist: true)
            : builder.AddParameter(scopedName, secret: true);
        return new Projection(parameter);
    }

    private sealed class Projection(IResourceBuilder<ParameterResource> masterKey) : IntoChatOSModuleProjection
    {
        public override void Apply<TResource>(IResourceBuilder<TResource> builder)
            => builder.WithEnvironment(IntoChatOSNames.MasterKeyEnvironmentVariable, masterKey);
    }
}
