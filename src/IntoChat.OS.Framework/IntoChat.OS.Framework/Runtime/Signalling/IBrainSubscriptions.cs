using IntoChat.OS.Sdk;

namespace IntoChat.OS.Framework;

// Silo code and the test harness that need a handle (completion, explicit disposal). Programs use IIntoChatOS.On.
public interface IBrainSubscriptions
{
    Task<ISignalSubscription<T>> SubscribeAsync<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal;
}
