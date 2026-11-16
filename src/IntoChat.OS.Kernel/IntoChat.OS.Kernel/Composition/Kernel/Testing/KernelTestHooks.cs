using System.Runtime.CompilerServices;
using System.Text.Json;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Kernel.Composition.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Composition.Kernel.Testing;

// The kernel composition's own test helper (06-09): facts that drive a runtime host grain directly (fire a durable reminder as
// the reminder service would, force activation collection, wait for an activation, deactivate a plain neuron) reach Orleans
// here, so no test project holds a grain factory. Not a production entry: nothing in a product host calls it.
public static class KernelTestHooks
{
    // The brain-affine host grain type of a plain neuron (DigitalBrain.Runtime.Orleans NeuronTypeRegistry).
    public const string BrainAffineHost = "neuron.host";

    // The host grain id of neuron: by the runtime's policy for its loaded [Neuron] class, else the brain-affine host (a cluster
    // client that does not load the module's classes, as the Kernel E2E's client is).
    public static GrainId HostOf(DigitalBrain.NeuronId neuron, string? hostType = null)
        => GrainId.Create(hostType ?? KernelInvocationSink.HostTypeOfLoaded(neuron.Type) ?? BrainAffineHost, neuron.ToString());

    // The host activation of neuron, for ITestBrain.DeactivateAsync.
    public static IAddressable Host(IServiceProvider services, DigitalBrain.NeuronId neuron)
        => Grains(services).GetGrain(HostOf(neuron));

    // Delivers a reminder tick to a host grain exactly as the reminder service would (a queued or replayed delivery included).
    public static Task Remind(IServiceProvider services, GrainId host, string reminder, CancellationToken cancellationToken = default)
        => Grains(services).GetGrain<IRemindable>(host).ReceiveReminder(reminder, default, cancellationToken);

    public static Task Remind(IServiceProvider services, DigitalBrain.NeuronId neuron, string reminder, CancellationToken cancellationToken = default)
        => Remind(services, HostOf(neuron), reminder, cancellationToken);

    // Collects every idle activation in the cluster now.
    public static Task CollectIdleActivations(IServiceProvider services, CancellationToken cancellationToken = default)
        => Grains(services).GetGrain<IManagementGrain>(0).ForceActivationCollection(TimeSpan.Zero, cancellationToken);

    // Whether grain has an activation in the cluster.
    public static async Task<bool> IsActive(IServiceProvider services, GrainId grain, CancellationToken cancellationToken = default)
        => (await Grains(services).GetGrain<IManagementGrain>(0).GetActiveGrains(grain.Type, cancellationToken)).Contains(grain);

    // A caller-less sink to the brain-affine host grains: calls carry no neuron caller, as a test edge on a cluster client does.
    public static IInvocationSink HostSink(IServiceProvider services, string hostType = BrainAffineHost)
        => new CallerlessSink(Grains(services), hostType);

    // The client of a facts' web host shares the brain's grain factory, as a cluster client host composes it.
    public static IServiceCollection ShareGrains(this IServiceCollection target, IServiceProvider brain)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.AddSingleton(Grains(brain));
        return target;
    }

    private static IGrainFactory Grains(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<IGrainFactory>();
    }

    private sealed class CallerlessSink(IGrainFactory grains, string hostType) : IInvocationSink
    {
        private INeuronHost Host(DigitalBrain.NeuronId target) => grains.GetGrain<INeuronHost>(GrainId.Create(hostType, target.ToString()));

        public async Task<JsonElement?> Invoke(DigitalBrain.NeuronId target, RuntimeInvocation call, CancellationToken cancellationToken)
        {
            var host = Host(target);
            var interleaved = await host.IsInterleaved(call).WaitAsync(cancellationToken).ConfigureAwait(true);
            return await (interleaved ? host.InvokeInterleaved(call, cancellationToken) : host.Invoke(call, cancellationToken)).ConfigureAwait(false);
        }

        public async Task Post(DigitalBrain.NeuronId target, RuntimeInvocation call)
        {
            var host = Host(target);
            if (await host.IsInterleaved(call).ConfigureAwait(true)) { await host.PostInterleaved(call).ConfigureAwait(false); }
            else { await host.Post(call).ConfigureAwait(false); }
        }

        public async IAsyncEnumerable<JsonElement> Stream(DigitalBrain.NeuronId target, RuntimeInvocation call, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in Host(target).Stream(call, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(true)) { yield return item; }
        }
    }
}
