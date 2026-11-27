using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Identity.Local.Directory;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Secrets;
using IntoChat.OS.Modules.Identity.Accounts;

namespace IntoChat.OS.Authority.Rules;

public sealed class IdentityRulesFacts
{
    [Fact]
    public void EveryRuleNameIsPrefixedAndUnique()
    {
        var rules = typeof(AuthorityMirror).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(Rule)))
            .Select(CreateDefault)
            .ToArray();

        Assert.Equal(15, rules.Length);
        Assert.All(rules, r => Assert.True(
            r.Name.StartsWith("identity.", StringComparison.Ordinal) || r.Name.StartsWith("circles.", StringComparison.Ordinal),
            $"Unexpected rule prefix: {r.Name}"));
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
    public async Task DirectoryNeuronRefusesAccountOwnerConflict()
    {
        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system"));
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<DirectoryNeuron>();
        var directory = os.Get<IDirectory>(new NeuronId(new BrainId("system"), IDirectory.Type, "main"));

        await directory.EnsureAccount(new Account { AccountId = "acct-1", OwnerPrincipalId = "alice", Name = "Alice" });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            directory.EnsureAccount(new Account { AccountId = "acct-1", OwnerPrincipalId = "bob", Name = "Bob" }));
    }

    [Fact]
    public async Task VaultNeuronRefusesNonOwnerStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<DigitalBrain.Identity.Local.Custody.IMasterKeyCustody>(
            new DigitalBrain.Identity.Local.Custody.ConfiguredMasterKeyCustody(System.Text.Encoding.UTF8.GetBytes("test-master-key-rules")));
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<DigitalBrain.Identity.Local.Vault.VaultNeuron>();
        var vault = os.Get<DigitalBrain.Identity.Local.Vault.IVault>(new NeuronId(new BrainId("system"), DigitalBrain.Identity.Local.Vault.IVault.Type, "alice"));

        using var bob = os.Edge.Enter(os.Edge.RelayedPerson("bob", "bob", new BrainId("system")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vault.Set("api", "API", "secret"));
    }
}
