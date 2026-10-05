namespace DigitalBrain.Apps;

// A module that gives apps durable storage (Postgres tables today) registers one of these.
// After every deployment the hook carries storage ownership to the app's current keys; on
// uninstall it retires everything the app owned. The app grain never knows which storage
// systems exist — a hook that has nothing for this app no-ops cheaply.
public interface IAppStorageHook
{
    Task OnDeployed(string brainId, string appKey, string[] storageKeys, CancellationToken cancellationToken);
    Task OnRetired(string brainId, string appKey, string[] storageKeys, CancellationToken cancellationToken);
}
