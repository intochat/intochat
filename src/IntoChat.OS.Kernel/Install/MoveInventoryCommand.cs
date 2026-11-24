using System.Text.Json;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Kernel.Composition.Kernel;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Kernel.Install;

// The operator's read-only inventory for the cloud's record move (cloud/OS cluster split): "move-inventory --home <home> --out <dir>".
// It reads the OS install's brain store and writes the three lists the cloud's move-plan and principal-links export take:
//   brains.txt      one brain id per line: "system" and every brain of the identity directory
//   principals.txt  one principal id per line: every principal of the identity directory
//   machines.txt    one "<brain> <machine id>" per line: every device a brain enrolls and has not revoked
// With machines.txt the cloud keeps each device's machine credential, so no device has to enroll again. It changes no record; like
// move-records it claims each brain store as its writer, so it runs with the apps stopped. The master key comes from the environment.
public static class MoveInventoryCommand
{
    public const string Verb = "move-inventory";
    private const string DirectoryType = "identity.directory";
    private const string EnrollmentType = "devices.enrollment";
    private const string SystemBrain = "system";

    public static bool IsInventory(string[] args) => args is [Verb, ..];

    public static Task<int> Run(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
        => Run(args, new ConfigurationBuilder().AddEnvironmentVariables().Build(), output, error, ct);

    public static async Task<int> Run(string[] args, IConfiguration configuration, TextWriter output, TextWriter error, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args is not [Verb, "--home", var home, "--out", var folder])
        {
            await error.WriteLineAsync($"usage: {Verb} --home <home> --out <dir>".AsMemory(), ct);
            return 64;
        }
        if (RecordMoveCommand.Custody(configuration) is not { } custody)
        {
            await error.WriteLineAsync($"{Verb}: IntoChatOS__MasterKey is not set; the brain store cannot be opened.".AsMemory(), ct);
            return 1;
        }
        try
        {
            var directory = await BrainRecordMove.Read(home, [SystemBrain], new BrainRecordRule(Neuron: DirectoryType), configuration, OsRuntimeOptions.Value, ct, custody);
            if (directory.Select(record => Parse(record.Bytes)).FirstOrDefault(state => state is not null && Find(state.Value, "principals") is not null) is not { } state)
            {
                await error.WriteLineAsync($"{Verb}: no identity directory in the brain store of '{home}'.".AsMemory(), ct);
                return 1;
            }
            string[] brains = [.. Ids(state, "brains", "brainId").Append(SystemBrain).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            string[] principals = [.. Ids(state, "principals", "principalId").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            var enrollments = await BrainRecordMove.Read(home, brains, new BrainRecordRule(Neuron: EnrollmentType), configuration, OsRuntimeOptions.Value, ct, custody);
            string[] machines = [.. enrollments
                .SelectMany(record => Parse(record.Bytes) is { } enrollment ? LiveMachines(enrollment).Select(machine => record.Brain + " " + machine) : [])
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            Directory.CreateDirectory(folder);
            await File.WriteAllLinesAsync(Path.Combine(folder, "brains.txt"), brains, ct);
            await File.WriteAllLinesAsync(Path.Combine(folder, "principals.txt"), principals, ct);
            await File.WriteAllLinesAsync(Path.Combine(folder, "machines.txt"), machines, ct);
            await output.WriteLineAsync($"{Verb}: {brains.Length} brains, {principals.Length} principals, {machines.Length} enrolled machines -> {folder}".AsMemory(), ct);
            return 0;
        }
        catch (Exception problem) when (problem is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"{Verb}: {problem.Message}".AsMemory(), ct);
            return 1;
        }
    }

    private static JsonElement? Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    // The stored state's JSON names follow the runtime protocol; matched without regard to case.
    private static JsonElement? Find(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { return property.Value; }
        }
        return null;
    }

    private static IEnumerable<string> Ids(JsonElement state, string array, string id)
        => Find(state, array) is { ValueKind: JsonValueKind.Array } rows
            ? rows.EnumerateArray().Select(row => row.ValueKind == JsonValueKind.Object && Find(row, id) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null)
                .OfType<string>().Where(value => value.Length > 0)
            : [];

    private static IEnumerable<string> LiveMachines(JsonElement enrollment)
        => Find(enrollment, "devices") is { ValueKind: JsonValueKind.Array } devices
            ? devices.EnumerateArray()
                .Where(device => device.ValueKind == JsonValueKind.Object && Find(device, "revoked") is not { ValueKind: JsonValueKind.True })
                .Select(device => Find(device, "machineId") is { ValueKind: JsonValueKind.String } value ? value.GetString() : null)
                .OfType<string>().Where(value => value.Length > 0 && !value.Any(char.IsWhiteSpace))
            : [];
}
