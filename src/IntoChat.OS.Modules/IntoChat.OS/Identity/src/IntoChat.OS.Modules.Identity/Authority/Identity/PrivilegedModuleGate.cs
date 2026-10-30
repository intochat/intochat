using DigitalBrain.Runtime.Http;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Identity;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Identity;

// The runtime layer of the privileged-module restriction. The seams are registered under a host-only key, so a module
// cannot resolve them from the container; it asks here and is answered only when its id is in
// IntoChatOS:Identity:PrivilegedModules.
internal sealed class PrivilegedModuleGate(IServiceProvider services, IOptions<IdentityHostOptions> options) : IPrivilegedServices
{
    internal const string HostKey = "intochat.host";

    public T For<T>(ModuleId module) where T : class
    {
        if (typeof(T) != typeof(ISessionIssuer) && typeof(T) != typeof(IIdentityProvisioning))
        { throw new ArgumentException($"{typeof(T).Name} is not a privileged identity seam.", nameof(module)); }
        if (!options.Value.PrivilegedModules.Contains(module.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                $"Module '{module}' is not listed in {IdentityHostOptions.Key(nameof(IdentityHostOptions.PrivilegedModules))}; it cannot use {typeof(T).Name}.");
        }

        return services.GetRequiredKeyedService<T>(HostKey);
    }
}
