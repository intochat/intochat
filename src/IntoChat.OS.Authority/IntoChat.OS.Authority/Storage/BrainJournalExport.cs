using System.Reflection;
using System.Text.Json;

namespace IntoChat.OS.Authority.Storage;

// The customer export of a brain scope. The default-deny state policy and its hard-deny list stay inside Authority.
public static class BrainJournalExport
{
    // The assembly of the platform's own states, which are never exported.
    public static Assembly StateAssembly => typeof(BrainJournalExport).Assembly;

    public static bool Allows(string stateName, Type stateType) => StateExportPolicy.Default.Allows(stateName, stateType);

    public static async Task WriteAsync(IServiceProvider services, string scope, Func<string, string, byte[], object?> decode, Stream destination, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        var logs = (BrainLogs)(services.GetService(typeof(BrainLogs)) ?? throw new InvalidOperationException("Brain storage is not composed."));
        var data = await (await logs.For(scope).WaitAsync(ct)).Export(decode, ct);
        await JsonSerializer.SerializeAsync(destination, data, cancellationToken: ct);
    }
}
