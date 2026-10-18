using IntoChat.OS.Framework;
using Orleans.Concurrency;
using Orleans.Metadata;

namespace IntoChat.OS.Flutter.Chat;

// The observer only queues this non-neuron turn, so a chat publishing its selection
// never waits for the composer to call back into it.
[Alias("ui.composer-reconciliation"), DefaultGrainType(UIVocabulary.ComposerType)]
internal interface IComposerReconciliation : IGrainWithStringKey
{
    [OneWay] Task Reconcile(string chat);
}

[Alias("ui.composer-migration"), DefaultGrainType(UIVocabulary.ComposerType)]
public interface IComposerMigration : IGrainWithStringKey
{
    Task AdoptFollow(string chat);
}

internal sealed class ComposerActivation : IActivationHandler<ComposerNeuron>
{
    public Task Activated(ComposerNeuron neuron, CancellationToken cancellationToken) => neuron.ActivateFollowing();
}
