using System.Reflection;
using IntoChat.OS.Framework;
using Xunit;

namespace IntoChat.OS.Runtime.InMemory.Tests.Unit;

// Neuron.Attach is a public runtime hook (02-09a): one context per neuron, never swapped.
public sealed class NeuronAttachFacts
{
    private sealed class Probe : Neuron;

    public class ContextProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException();
    }

    [Fact]
    public void ASecondAttachThrows()
    {
        var neuron = new Probe();
        neuron.Attach(DispatchProxy.Create<INeuronContext, ContextProxy>());
        Assert.Throws<InvalidOperationException>(() => neuron.Attach(DispatchProxy.Create<INeuronContext, ContextProxy>()));
    }

    [Fact]
    public void ANullContextIsRejected()
        => Assert.Throws<ArgumentNullException>(() => new Probe().Attach(null!));
}
