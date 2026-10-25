using IntoChat.OS.Sdk.Modules;

namespace IntoChat.OS.Sdk.Identity;

// How a module obtains a privileged identity seam (ISessionIssuer, IIdentityProvisioning). The host resolves it
// only for module ids listed in IntoChatOS:Identity:PrivilegedModules and fails closed for every other module.
public interface IPrivilegedServices
{
    T For<T>(ModuleId module) where T : class;
}
