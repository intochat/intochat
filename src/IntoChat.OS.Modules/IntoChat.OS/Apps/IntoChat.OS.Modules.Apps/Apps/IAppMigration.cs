using Orleans.Metadata;

namespace IntoChat.OS.Apps;

// Host infrastructure only: deliberately not an INeuron contract exposed to scripts or HTTP.
[Alias("apps.migration"), DefaultGrainType(IntoChat.OS.Framework.AppGrainType.Name)]
public interface IAppMigration : IGrainWithStringKey
{
    Task Pause(string migration);
    Task<AppState> Archive(string migration);
    Task Adopt(string migration, string source);
    Task Redirect(string migration, string target);
    Task Resume(string migration);
    Task Accept(string operation, string input, Guid requestId);
    Task MergeRequests(RetainedSignal[] requests);
}

// A host can gate a feature replacement on adoption of its existing data. A failed before/after
// call is retried by the ordinary feature reconciler; implementations must be resumable.
public interface IFeatureMigration
{
    Task PrepareBrain(string brain, CancellationToken ct);
    Task<bool> Required(string brain, string name, AppSnapshot current, CancellationToken ct);
    Task<AppBundle> BeforeLoad(string brain, string name, AppBundle bundle, CancellationToken ct);
    Task AfterLoad(string brain, string name, AppBundle bundle, CancellationToken ct);
}
