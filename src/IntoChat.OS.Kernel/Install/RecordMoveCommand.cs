using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Kernel.Composition.Kernel;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Kernel.Install;

// The operator's verb for the one-time move of the cloud's records out of the OS install's brain store (cloud/OS cluster split,
// step 6): "move-records <plan.json> <plan|backup|copy|remove-source>". The plan names the two Homes, the backup directory, the
// brains and the rules (BrainMovePlan); the master key comes from the environment (IntoChatOS__MasterKey), as for the host. Run it
// with both apps stopped; it claims each brain store as its writer.
public static class RecordMoveCommand
{
    public const string Verb = "move-records";

    public static bool IsMove(string[] args) => args is [Verb, ..];

    public static Task<int> Run(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
        => Run(args, new ConfigurationBuilder().AddEnvironmentVariables().Build(), output, error, ct);

    public static async Task<int> Run(string[] args, IConfiguration configuration, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is not [Verb, var file, var phaseName] || Phase(phaseName) is not { } phase)
        {
            await error.WriteLineAsync($"usage: {Verb} <plan.json> <plan|backup|copy|remove-source>".AsMemory(), ct);
            return 64;
        }
        BrainMovePlan? plan;
        try { plan = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(file, ct), RecordMoveJson.Default.BrainMovePlan); }
        catch (Exception problem) when (problem is IOException or JsonException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"{Verb}: cannot read the plan '{file}': {problem.Message}".AsMemory(), ct);
            return 66;
        }
        if (plan is null)
        {
            await error.WriteLineAsync($"{Verb}: the plan '{file}' is empty.".AsMemory(), ct);
            return 66;
        }
        try
        {
            var lines = await BrainRecordMove.Run(plan, phase, configuration, OsRuntimeOptions.Value, ct);
            foreach (var line in lines)
            {
                await output.WriteLineAsync($"{phaseName} {line.Brain}: selected {line.Selected}, copied {line.Copied}, already there {line.AlreadyThere}, removed {line.Removed}, conflicts {line.Conflicts.Length}".AsMemory(), ct);
                foreach (var conflict in line.Conflicts) { await output.WriteLineAsync(("  conflict " + conflict).AsMemory(), ct); }
            }
            return lines.Any(line => line.Conflicts.Length > 0) ? 2 : 0;
        }
        catch (Exception problem) when (problem is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"{Verb} {phaseName}: {problem.Message}".AsMemory(), ct);
            return 1;
        }
    }

    private static BrainMovePhase? Phase(string name) => name switch
    {
        "plan" => BrainMovePhase.Plan,
        "backup" => BrainMovePhase.Backup,
        "copy" => BrainMovePhase.Copy,
        "remove-source" => BrainMovePhase.RemoveSource,
        _ => null,
    };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(BrainMovePlan))]
internal sealed partial class RecordMoveJson : JsonSerializerContext;
