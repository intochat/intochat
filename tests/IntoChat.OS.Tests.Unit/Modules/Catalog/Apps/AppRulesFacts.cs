using System.Reflection;
using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppRulesFacts
{
    private static TestOS CreateTestOS()
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        var provider = services.BuildServiceProvider();
        return TestOS.Local(o => o.Services = provider).Register<AppNeuron>();
    }

    private static Rule CreateDefault(Type type)
    {
        var ctor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
        var args = ctor.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        return (Rule)ctor.Invoke(args);
    }

    [Fact]
    public void EveryRuleNameIsPrefixedAndUnique()
    {
        var rules = typeof(AppNeuron).Assembly.GetTypes().Where(t => t.IsAssignableTo(typeof(Rule)) && !t.IsAbstract).ToArray();
        var names = rules.Select(t => CreateDefault(t).Name).ToArray();
        Assert.All(names, n => Assert.StartsWith("apps.", n, StringComparison.Ordinal));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(27, rules.Length);
    }

    [Fact]
    public async Task DuplicateNameIsRejectedWithTheRuleName()
    {
        await using var os = CreateTestOS();
        var brain = new BrainId("owner");
        var id = new NeuronId(brain, IntoChat.OS.Connections.AppGrainType.Name, "apps/notes");
        var app = os.Get<IApp>(id);
        var manifest = new AppManifest("Notes", "Keep notes", [], [], []);
        await app.Create(manifest, "spec");

        var error = await Assert.ThrowsAsync<RuleViolationException>(() => app.Create(manifest, "spec"));
        Assert.Equal(["apps.name-free"], error.Rejected.Rules);
    }

    [Fact]
    public async Task ConfigureRefusesReservedSettingKeys()
    {
        await using var os = CreateTestOS();
        var id = new NeuronId(new BrainId("owner"), IntoChat.OS.Connections.AppGrainType.Name, "apps/notes");
        var app = os.Get<IApp>(id);
        await app.Create(new AppManifest("Notes", "Keep notes", [], [], []), "spec");

        var error = await Assert.ThrowsAsync<RuleViolationException>(() => app.Configure(new() { [AppTrust.CompiledTypeSetting] = "Evil, Evil" }, []));
        Assert.Equal(["apps.settings-reserved"], error.Rejected.Rules);
    }

    [Fact]
    public async Task RefusedTurnLeavesTheJournalUnchanged()
    {
        await using var os = CreateTestOS();
        var brain = new BrainId("owner");
        var id = new NeuronId(brain, IntoChat.OS.Connections.AppGrainType.Name, "apps/notes");
        var app = os.Get<IApp>(id);
        var manifest = new AppManifest("Notes", "Keep notes", [], [], []);
        var created = await app.Create(manifest, "initial spec");

        var heard = new List<Rejected>();
        await using var listener = await os.ClusterListeners.Listen<Rejected>("test", (source, signal, _) =>
        {
            if (source == id) { heard.Add(signal); }
            return Task.CompletedTask;
        });

        var entriesBefore = os.Journal(brain).Signals(id).Count;

        var error = await Assert.ThrowsAsync<RuleViolationException>(() => app.Create(manifest, "other spec"));
        Assert.Equal("Create", error.Rejected.Operation);
        Assert.Equal(["apps.name-free"], error.Rejected.Rules);

        await os.Idle();
        var rejected = Assert.Single(heard);
        Assert.Equal("Create", rejected.Operation);
        Assert.Equal(["apps.name-free"], rejected.Rules);

        Assert.Equal(entriesBefore, os.Journal(brain).Signals(id).Count);
        var current = await app.Read();
        Assert.Equal(created.Version, current.Version);
        Assert.Equal("initial spec", current.Spec);
    }

    [Fact]
    public async Task AnEmitForAnUndeclaredOperationIsRefused()
    {
        await using var os = CreateTestOS();
        var id = new NeuronId(new BrainId("owner"), IntoChat.OS.Connections.AppGrainType.Name, "apps/notes");
        var app = os.Get<IApp>(id);
        await app.Create(new AppManifest("Notes", "Keep notes", [new("add", "Add a note.")], [], []), "spec");

        var error = await Assert.ThrowsAsync<RuleViolationException>(() => app.Emit("wipe", "\"all\""));
        Assert.Equal(["apps.operation"], error.Rejected.Rules);
        Assert.DoesNotContain(os.Journal(id.Brain).Signals(id), entry => entry.Signal is Signals.AppRequested);
    }

    [Fact]
    public async Task AnEmitRunsTheSameRulesAsRequest()
    {
        await using var os = CreateTestOS();
        var id = new NeuronId(new BrainId("owner"), IntoChat.OS.Connections.AppGrainType.Name, "apps/notes");
        var app = os.Get<IApp>(id);
        await app.Create(new AppManifest("Notes", "Keep notes", [new("add", "Add a note.")], [], []), "spec");
        var oversized = new string('x', 1_000_000);

        var byRequest = await Assert.ThrowsAsync<RuleViolationException>(() => app.Request("add", oversized));
        var byEmit = await Assert.ThrowsAsync<RuleViolationException>(() => app.Emit("add", oversized));
        Assert.Equal(byRequest.Rejected.Rules, byEmit.Rejected.Rules);
        Assert.Equal(byRequest.Rejected.Operation, byEmit.Rejected.Operation);
        Assert.NotEmpty(byEmit.Rejected.Rules);
        await os.Idle();
        Assert.DoesNotContain(os.Journal(id.Brain).Signals(id), entry => entry.Signal is Signals.AppRequested);

        await app.Emit("add", "\"milk\"");
        await os.Idle();
        Assert.Contains(os.Journal(id.Brain).Signals(id), entry => entry.Signal is Signals.AppRequested { Operation: "add", Input: "\"milk\"" });
    }
}
