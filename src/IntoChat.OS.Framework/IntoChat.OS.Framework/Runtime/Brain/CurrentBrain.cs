namespace IntoChat.OS.Framework;

// What silo code gets for IIntoChatOS: the brain the stamped caller is in.
internal sealed class CurrentBrain(IIntoChatOSBrains brains, IKernelView kernel) : IIntoChatOS
{
    public T Get<T>(NeuronId id) where T : class => brains.Get<T>(id.Name);

    public T Neuron<T>(NeuronId relative) where T : class => Get<T>(relative.Within(BrainScope.CurrentId()));

    public IAsyncEnumerable<T> On<T>(object neuron, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : DigitalBrain.Signal
        => brains.On<T>(neuron, onSubscribed, cancellationToken);

    public IBrain Self => brains.Open(BrainScope.CurrentId()).Self;

    public IKernelView Kernel => kernel;
}
