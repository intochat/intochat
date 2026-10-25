using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework;

// What silo code gets for IIntoChatOS. Transitional: keys are still module-composed, so Get
// resolves the raw key and only Self needs the stamped caller.
internal sealed class CurrentBrain(IIntoChatOSBrains brains, IKernelView kernel) : IIntoChatOS, IntoChat.OS.Runtime.IPlainClient
{
    public T Get<T>(NeuronId id) where T : INeuron => brains.Grain<T>(id.Name);

    public T Neuron<T>(NeuronId relative) where T : INeuron => Get<T>(relative.Within(BrainScope.CurrentId()));

    public IAsyncEnumerable<T> On<T>(INeuron neuron, CancellationToken cancellationToken = default) where T : Signal
        => brains.On<T>(neuron, cancellationToken);

    public IAsyncEnumerable<T> On<T>(INeuron neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
        => brains.On<T>(neuron, onSubscribed, cancellationToken);

    public IBrain Self => brains.Open(BrainScope.CurrentId()).Self;

    public IKernelView Kernel => kernel;

    // Transitional (06-07): plain neurons through the cluster client's seam (Client.Orleans BrainClient).
    private IntoChat.OS.Runtime.IPlainClient Plain => brains as IntoChat.OS.Runtime.IPlainClient
        ?? throw new NotSupportedException("This host's brains do not reach plain neurons.");

    object IntoChat.OS.Runtime.IPlainClient.GetPlain(Type contract, NeuronId id) => Plain.GetPlain(contract, id);

    IAsyncEnumerable<T> IntoChat.OS.Runtime.IPlainClient.OnPlain<T>(object source, Func<Task> onSubscribed, CancellationToken cancellationToken)
        => Plain.OnPlain<T>(source, onSubscribed, cancellationToken);
}
