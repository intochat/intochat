using DigitalBrain.Capabilities;

namespace IntoChat.Cloud.Auth;

// The three things Auth needs from the host that live below DigitalBrain's neutral surface: who is calling, who the module is when it
// calls a capability, and how a request from the HTTP edge reaches the module's own [Internal] neurons. The cloud host binds it
// over its runtime (09-09); Auth itself references no Orleans.
public interface IAuthHostSeam
{
    // The caller the runtime stamped on the current neuron call (the signed-in person), or null.
    CapabilityCaller? CurrentCaller { get; }

    // Stamps the auth module itself as the capability caller until disposed, so the runtime checks the manifest's grants and secrets.
    IDisposable StampModuleCaller(CapabilityCaller module);

    // Runs a call from the HTTP edge as a module call: a stamped request may reach the module's [Internal] neurons only inside it.
    Task<T> InModuleCall<T>(Func<Task<T>> call);
}
