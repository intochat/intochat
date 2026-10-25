using System.Reflection;
using System.Text.Json;
using IntoChat.OS;
using Orleans;
using DigitalBrain.State;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Brain export is default-deny. Every type held in [PersistentState] storage is decided: [ExportableState]
// (customer data the portable export may copy) xor listed in NonExportableStates.json with a reason.
// A new state type fails here until its owner decides, so nothing is exported by accident.
public sealed class ExportableStateFacts
{
    private sealed record Decision(string Type, string State, string Reason);
    private sealed record Baseline(string Type, string State, bool ExportedToday);

    private static readonly string[] BillingStates = ["LedgerState", "UsageState", "TraceBudgetState", "IntentUsageStorage", "BrainComputeState", "TraceJournalState"];

    private static (Type State, string Name)[] Persisted() => ProductionArchitecture.Types
        .Where(type => !type.IsAbstract && !type.ContainsGenericParameters)
        .SelectMany(type => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        .SelectMany(constructor => constructor.GetParameters())
        .Where(parameter => parameter.IsDefined(typeof(PersistentStateAttribute), false)
            && parameter.ParameterType.IsGenericType && parameter.ParameterType.GetGenericTypeDefinition() == typeof(IPersistentState<>))
        .Select(parameter => (parameter.ParameterType.GetGenericArguments()[0], parameter.GetCustomAttribute<PersistentStateAttribute>()!.StateName))
        .Distinct().ToArray();

    private static T[] Load<T>(string file) => JsonSerializer.Deserialize<T[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static bool Exportable(Type type) => type.IsDefined(typeof(ExportableStateAttribute), false);

    private static string[] HardDenied()
    {
        var policy = ProductionArchitecture.Assemblies.Single(assembly => assembly.GetName().Name == "IntoChat.OS.Authority")
            .GetType("IntoChat.OS.Authority.Storage.StateExportPolicy", throwOnError: true)!;
        var set = (IEnumerable<string>)policy.GetField("HardDenied", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
        return [.. set];
    }

    [Fact]
    public void EveryPersistedStateIsDecided()
    {
        var persisted = Persisted();
        Assert.NotEmpty(persisted);
        var denied = Load<Decision>("NonExportableStates.json");
        Assert.All(denied, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Reason), entry.Type + " has no reason"));
        var listed = denied.Select(entry => entry.Type).ToHashSet(StringComparer.Ordinal);
        var names = persisted.Select(item => item.State.FullName!).ToHashSet(StringComparer.Ordinal);

        var undecided = persisted.Where(item => !Exportable(item.State) && !listed.Contains(item.State.FullName!))
            .Select(item => $"{item.State.FullName} (state '{item.Name}')").Order().ToArray();
        Assert.True(undecided.Length == 0,
            "Persisted state types with no export decision. Mark [ExportableState] (customer data) or list in NonExportableStates.json with a reason:\n"
            + string.Join('\n', undecided));
        var both = persisted.Where(item => Exportable(item.State) && listed.Contains(item.State.FullName!)).Select(item => item.State.FullName!).ToArray();
        Assert.True(both.Length == 0, "Both [ExportableState] and listed as non-exportable:\n" + string.Join('\n', both));
        var stale = listed.Except(names).Order().ToArray();
        Assert.True(stale.Length == 0, "NonExportableStates.json lists types that are no longer persisted:\n" + string.Join('\n', stale));
    }

    [Fact]
    public void LedgerUsageBudgetAreNotExportable()
    {
        foreach (var name in BillingStates)
        {
            var type = Assert.Single(ProductionArchitecture.Types, candidate => candidate.Name == name);
            Assert.False(Exportable(type), name + " is billing/usage state, not customer data");
            Assert.Contains(Persisted(), item => item.State == type);
        }
    }

    [Fact]
    public void HardDeniedNamesNeverAttributed()
    {
        var denied = HardDenied();
        Assert.Contains("reminders", denied);
        var offenders = Persisted().Where(item => denied.Contains(item.Name, StringComparer.Ordinal) && Exportable(item.State))
            .Select(item => $"{item.State.FullName} persisted as '{item.Name}'").ToArray();
        Assert.True(offenders.Length == 0, "Hard-denied state names must never be attributed:\n" + string.Join('\n', offenders));
    }

    [Fact]
    public void AttributedSetIsTodaysExportMinusBillingStates()
    {
        var baseline = Load<Baseline>("ExportedStateBaseline.json");
        var expected = baseline.Where(row => row.ExportedToday && !BillingStates.Contains(row.Type.Split('.')[^1]))
            .Select(row => row.Type).Order().ToArray();
        var actual = Persisted().Where(item => Exportable(item.State)).Select(item => item.State.FullName!).Distinct().Order().ToArray();
        Assert.Equal(expected, actual);
    }
}
