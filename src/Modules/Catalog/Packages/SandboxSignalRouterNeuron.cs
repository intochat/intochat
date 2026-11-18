using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;
using DigitalBrain.Neurons;
using IntoChat.Sdk;

namespace IntoChat.OS.Modules.Apps;

/// <summary>
/// Brain-scoped DigitalBrain synapse router bridging brain signals (<c>windows.file-changed</c>,
/// <c>notifications.sent</c>) into the hardened sandbox worker for subscribed <see cref="AppPackage"/> apps.
/// </summary>
[Shared]
[NeuronAlias(TypeName), DefaultNeuronType(TypeName)]
internal interface ISandboxSignalRouter
{
    const string TypeName = "apps.sandbox-router";
    const string DefaultKey = "default";

    static NeuronId Key(string brainId) => new(new BrainId(brainId), TypeName, DefaultKey);

    Task BindSignalsAsync(string[] signals, CancellationToken cancellationToken = default);
}

[Neuron(ISandboxSignalRouter.TypeName)]
internal sealed class SandboxSignalRouterNeuron(ISandboxRuntimeBridge? bridge = null)
    : Neuron, ISandboxSignalRouter
{
    public async Task BindSignalsAsync(string[] signals, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(signals);

        foreach (var signal in signals)
        {
            if (string.Equals(signal, "windows.file-changed", StringComparison.OrdinalIgnoreCase))
            {
                await Context.Wires.Ensure<FileChanged>(new NeuronId(Id.Brain, "windows.folder", "folders")).ConfigureAwait(false);
            }
            else if (string.Equals(signal, "notifications.sent", StringComparison.OrdinalIgnoreCase))
            {
                await Context.Wires.Ensure<NotificationSent>(new NeuronId(Id.Brain, "notifications.inbox", "inbox")).ConfigureAwait(false);
            }
        }
    }

    [On]
    public Task OnFileChanged(FileChanged change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (bridge is null)
        {
            return Task.CompletedTask;
        }

        var payloadJson = System.Text.Encoding.UTF8.GetString(DigitalBrain.Invocation.RuntimeProtocol.SerializeToUtf8Bytes(change, typeof(FileChanged)));
        return bridge.ForwardSignalAsync(Id.Brain.Value, "windows.file-changed", payloadJson);
    }

    [On]
    public Task OnNotificationSent(NotificationSent sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        if (bridge is null)
        {
            return Task.CompletedTask;
        }

        var payloadJson = System.Text.Encoding.UTF8.GetString(DigitalBrain.Invocation.RuntimeProtocol.SerializeToUtf8Bytes(sent, typeof(NotificationSent)));
        return bridge.ForwardSignalAsync(Id.Brain.Value, "notifications.sent", payloadJson);
    }
}
