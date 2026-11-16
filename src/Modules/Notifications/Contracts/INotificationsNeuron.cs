using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Modules.Notifications;

[Shared]
[NeuronAlias(TypeName), DefaultNeuronType(TypeName)]
public interface INotificationsInbox : IntoChat.Sdk.INotifications
{
    const string TypeName = "notifications.inbox";
    const string DefaultKey = "inbox";

    static NeuronId Key(string brainId) => new(new BrainId(brainId), TypeName, DefaultKey);

    Task ClearAsync(CancellationToken cancellationToken = default);

    [ReadOnlyCall]
    Task<int> UnreadCountAsync(CancellationToken cancellationToken = default);
}
