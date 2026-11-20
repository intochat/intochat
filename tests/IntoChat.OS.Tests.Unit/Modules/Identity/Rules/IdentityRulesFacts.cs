using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using IntoChat.OS.Modules.Identity.Authority;
using IntoChat.OS.Modules.Identity.Contacts;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Identity.Secrets;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Contacts;

namespace IntoChat.OS.Authority.Rules;

public sealed class IdentityRulesFacts
{
    [Fact]
    public void EveryRuleNameIsPrefixedAndUnique()
    {
        var rules = typeof(IdentityDirectoryGrain).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(Rule)))
            .Select(CreateDefault)
            .ToArray();

        Assert.Equal(17, rules.Length);
        Assert.All(rules, r => Assert.StartsWith("identity.", r.Name, StringComparison.Ordinal));
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
    public async Task DirectoryNeuronRefusesInvalidAndTakenPrincipalsWithRules()
    {
        // The system brain is platform territory: its directory, marketplace and circles are reached by platform code.
        using var kernel = IntoChat.OS.Tests.TestEdge.Authority.EnterKernel(new BrainId("system"));
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions { Posture = IdentityPosture.Open }));
        services.AddSingleton<AuthorityMirror>();
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<IdentityDirectoryGrain>();
        var directory = os.Get<IDirectoryStore>(new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main"));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => directory.RegisterPrincipal("owner", "Owner"));
        Assert.Contains("identity.principal-id-available", ex.Rejected.Rules);
    }

    [Fact]
    public async Task ContactsNeuronRefusesInvalidListAndEmptyPrincipalWithRules()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        await using var os = TestOS.Local();
        os.Register<ContactsNeuron>();
        var contacts = os.Get<IContactsNeuron>(new NeuronId(new BrainId("owner"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => contacts.Add("friends", new ContactEntry { Principal = "" }));
        Assert.Contains("identity.contact-principal-not-empty", ex.Rejected.Rules);
    }

    [Fact]
    public async Task VaultNeuronRefusesNonOwnerStoreWithRule()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IKeyWrapper, PassThroughKeyWrapper>();
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<OsVaultNeuron>();
        var vault = os.Get<IOsVault>(new NeuronId(new BrainId("system"), IOsVault.Type, "alice"));

        using var bob = os.Edge.Enter(os.Edge.RelayedPerson("bob", "bob", new BrainId("system")));
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => vault.Set("api", "API", "secret"));
        Assert.Contains("identity.caller-can-store-secret", ex.Rejected.Rules);
    }

    private sealed class PassThroughKeyWrapper : IKeyWrapper
    {
        public string Wrap(byte[] key) => Convert.ToBase64String(key);
        public byte[] Unwrap(string wrappedKey) => Convert.FromBase64String(wrappedKey);
    }
}
