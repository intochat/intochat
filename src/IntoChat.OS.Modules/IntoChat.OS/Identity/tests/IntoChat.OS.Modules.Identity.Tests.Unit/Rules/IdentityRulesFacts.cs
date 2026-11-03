using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Authority.Connections;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Authority.Identity.Authority;
using IntoChat.OS.Authority.Identity.Contacts;
using IntoChat.OS.Authority.Secrets;
using IntoChat.OS.Connections;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Integrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Authority.Rules;

public sealed class IdentityRulesFacts
{
    [Fact]
    public void EveryRuleNameIsPrefixedAndUnique()
    {
        var rules = typeof(DirectoryNeuron).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsAssignableTo(typeof(Rule)))
            .Select(CreateDefault)
            .ToArray();

        Assert.Equal(22, rules.Length);
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
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AuthOptions { Posture = IdentityPosture.Open }));
        services.AddSingleton<AuthorityMirror>();
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<DirectoryNeuron>();
        var directory = os.Get<IDirectoryStore>(new NeuronId(new BrainId("system"), IDirectoryStore.Type, "main"));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => directory.RegisterPrincipal("owner", "Owner"));
        Assert.Contains("identity.principal-id-available", ex.Rejected.Rules);
    }

    [Fact]
    public async Task ContactsNeuronRefusesInvalidListAndEmptyPrincipalWithRules()
    {
        await using var os = TestOS.Local();
        os.Register<ContactsNeuron>();
        var contacts = os.Get<IContactsNeuron>(new NeuronId(new BrainId("owner"), IContactsNeuron.TypeName, IContactsNeuron.DefaultKey));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => contacts.Add("friends", new ContactEntry { Principal = "" }));
        Assert.Contains("identity.contact-principal-not-empty", ex.Rejected.Rules);
    }

    [Fact]
    public async Task ConnectionIndexNeuronRefusesForeignBrainKeyWithRule()
    {
        await using var os = TestOS.Local();
        os.Register<ConnectionIndexNeuron>();
        var index = os.Get<IConnectionIndex>(IConnectionIndex.Id("owner"));

        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => index.Track("other/connection/github/main"));
        Assert.Contains("identity.connection-belongs-to-brain", ex.Rejected.Rules);
    }

    [Fact]
    public async Task VaultNeuronRefusesNonOwnerStoreWithRule()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IKeyWrapper, PassThroughKeyWrapper>();
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<VaultNeuron>();
        var vault = os.Get<IVault>(new NeuronId(new BrainId("system"), IVault.Type, "alice"));

        var bob = CapabilityCallers.Of("bob", "bob", "bob");
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => vault.Set(bob, "api", "API", "secret"));
        Assert.Contains("identity.caller-can-store-secret", ex.Rejected.Rules);
    }

    private sealed class PassThroughKeyWrapper : IKeyWrapper
    {
        public string Wrap(byte[] key) => Convert.ToBase64String(key);
        public byte[] Unwrap(string wrappedKey) => Convert.FromBase64String(wrappedKey);
    }
}
