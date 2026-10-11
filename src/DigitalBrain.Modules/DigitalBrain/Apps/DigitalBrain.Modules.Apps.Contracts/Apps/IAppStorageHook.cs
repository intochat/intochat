namespace DigitalBrain.Apps;

// A module that gives apps durable storage (Postgres tables today) registers one of these.
// On uninstall it retires everything the app owned. The app grain never knows which storage
// systems exist — a hook that has nothing for this app no-ops cheaply.
public interface IAppStorageHook
{
    Task OnRetired(string brainId, string appKey, CancellationToken cancellationToken);

    // The app came into existence (created or installed); storage retired by an earlier delete starts fresh.
    Task OnCreated(string brainId, string appKey, CancellationToken cancellationToken) => Task.CompletedTask;

    // Throws when the app's stored data depends on the slot's current binding.
    Task RequireRebindable(string brainId, string appKey, string slot, CancellationToken cancellationToken) => Task.CompletedTask;
}
