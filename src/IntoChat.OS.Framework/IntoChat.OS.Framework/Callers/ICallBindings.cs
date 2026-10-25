using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// R146: whether an app call into another brain's neuron is admitted through a binding recorded when the app was
// shared (R134). CallFilter asks only for app callers outside their brain; with no source registered nothing is bound.
// The Connections assembly answers it from the connection bindings.
public interface ICallBindings
{
    ValueTask<bool> IsBound(CallRequest request, string callerScope, CancellationToken cancellationToken = default);
}
