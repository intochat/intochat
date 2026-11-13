using DigitalBrain.Capabilities;

namespace IntoChat.Cloud.Organisations;

// What Organisations needs from the host that lives below DigitalBrain's neutral surface. The cloud host binds it (09-09).
// This assembly references no Orleans.
public interface IOrganisationHost
{
    CapabilityCaller? CurrentCaller { get; }

    IDisposable StampModule();

    Task InModuleCall(Func<Task> call);

    Task<T> InModuleCall<T>(Func<Task<T>> call);
}
