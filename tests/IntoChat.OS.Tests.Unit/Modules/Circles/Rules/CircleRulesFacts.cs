using DigitalBrain;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using IntoChat.OS.Host.Primitives;

namespace IntoChat.OS.Modules.Circles.Tests.Unit.Rules;

public sealed class CircleRulesFacts
{
    [Fact]
    public void EveryRuleNameIsPrefixedAndUnique()
    {
        var rules = typeof(CircleNeuron).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(Rule)))
            .Select(CreateDefault)
            .ToArray();

        Assert.Equal(12, rules.Length);
        Assert.All(rules, r => Assert.StartsWith("circles.", r.Name, StringComparison.Ordinal));
        Assert.Equal(rules.Length, rules.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count());
    }

    private static Rule CreateDefault(Type type)
    {
        var ctor = type.GetConstructors().OrderBy(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        return (Rule)ctor.Invoke(args);
    }

    [Fact]
    public async Task CircleNeuronRefusesInvalidNameAndOutOfRangeComputeShareWithRules()
    {
        // circles.circle is cluster-internal: its founder reaches it relayed by a neuron, never directly from the edge.
        using var founder = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.RelayedPerson("c1", "c1", new BrainId("c1")));
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<CircleNeuron>>(NullLogger<CircleNeuron>.Instance);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<CircleNeuron>();
        var circle = os.Get<ICircle>(new NeuronId(new BrainId("c1"), CircleGrainType.Name, "circle"));

        var badName = await Assert.ThrowsAsync<RuleViolationException>(() => circle.Found("Bad Name!"));
        Assert.Contains("circles.name-valid", badName.Rejected.Rules);

        var badShare = await Assert.ThrowsAsync<RuleViolationException>(() => circle.SetComputeShare(-5));
        Assert.Contains("circles.compute-share-in-range", badShare.Rejected.Rules);
    }

    [Fact]
    public async Task InviteCodeAndKeyNeuronsRefuseCollisionsAndShortKeysWithRules()
    {
        // The system brain is platform territory: its directory, marketplace and circles are reached by platform code.
        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system"));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IInviteKey.Configuration] = Convert.ToBase64String(new byte[16]),
            })
            .Build());
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider())
            .Register<InviteCodeNeuron>()
            .Register<InviteKeyNeuron>();

        var code = os.Get<IInviteCode>(new NeuronId(new BrainId("system"), IInviteCode.Type, "hash-1"));
        await code.Bind("c1", "inv-1", DateTimeOffset.UtcNow.AddHours(1));
        var collision = await Assert.ThrowsAsync<RuleViolationException>(() => code.Bind("c1", "inv-2", DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Contains("circles.invite-code-available", collision.Rejected.Rules);

        var key = os.Get<IInviteKey>(new NeuronId(new BrainId("system"), IInviteKey.Type, "main"));
        var shortKey = await Assert.ThrowsAsync<RuleViolationException>(key.Get);
        Assert.Contains("circles.invite-hmac-key-length", shortKey.Rejected.Rules);
    }
}
