using Orleans.Concurrency;

namespace IntoChat.OS.Modules.Apps;

// Builds an app from a description in one call: the Author writes the spec, the Builder writes the
// behaviors, and the call answers the app once it is Live or Failed. A single call so a router can
// route to it and the shell can await one invocation.
[Alias("apps.studio"), Orleans.Metadata.DefaultGrainType("apps.studio")]
public interface IAppStudio : INeuron
{
    // Create the app from a description; answer it Drafted, without behaviors.
    [AlwaysInterleave, ResponseTimeout("00:10:00")] Task<AppSnapshot> Author(string request, CancellationToken ct = default);

    // Author if needed, then build the behaviors and answer the app once it is Live or Failed.
    [AlwaysInterleave, ResponseTimeout("00:10:00")] Task<AppSnapshot> Build(string request, CancellationToken ct = default);
}
