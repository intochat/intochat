using DigitalBrain;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using IntoChat.Cloud.Mail.Templates;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.Cloud.Mail.Tests;

public sealed class MailFacts
{
    [Theory]
    [InlineData("cloud", true)]
    [InlineData("local", false)]
    [InlineData("local+cloud", false)]
    [InlineData(null, false)]
    public void OnlyCloudLoadsMail(string? topology, bool loads) => Assert.Equal(loads, MailModule.LoadsUnder(topology));

    [Fact]
    public void TheManifestNamesTheAcsAndHashingSecrets()
    {
        var manifest = ModuleManifest.Of(typeof(MailModule));
        Assert.Equal("mail", manifest.Id);
        Assert.Contains(manifest.Secrets, secret => secret.Name == MailSecrets.AcsConnection);
        Assert.Contains(manifest.Secrets, secret => secret.Name == MailSecrets.RecipientHashing);
    }

    [Fact]
    public async Task ACodeAddressIsLimitedToFiveAnHourAndFortyTwoSecondsApart()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        var neuron = new MailRateNeuron(clock);
        var context = new FakeNeuronContext(new NeuronId(new BrainId("system-01"), IMailRate.Type, "mail-rate/h/SignupCode"));
        NeuronAttachment.Attach(neuron, context);
        await NeuronAttachment.Activate(neuron, CancellationToken.None);

        Assert.True((await neuron.TryAcquire(MailTemplateId.SignupCode)).Allowed);
        var cooled = await neuron.TryAcquire(MailTemplateId.SignupCode);
        Assert.False(cooled.Allowed);
        clock.Advance(TimeSpan.FromSeconds(42));
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(42));
            Assert.True((await neuron.TryAcquire(MailTemplateId.SignupCode)).Allowed);
        }

        clock.Advance(TimeSpan.FromSeconds(42));
        Assert.False((await neuron.TryAcquire(MailTemplateId.SignupCode)).Allowed);
    }

    [Fact]
    public void TheHasherIsStableAndHidesTheAddress()
    {
        var hasher = new HmacRecipientHasher();
        hasher.UseKey("test-key");
        var hash = hasher.Hash("mail-rate", "Ada@Example.com");
        Assert.Equal(hash, hasher.Hash("mail-rate", "ada@example.com"));
        Assert.DoesNotContain("ada", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHasherResolvesKeyFromBuiltServiceProvider()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var secrets = new StubSecrets();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddKeyedSingleton<DigitalBrain.Capabilities.Secrets.ISecrets>(
            services,
            DigitalBrain.Capabilities.Capability.Secrets.V1Id,
            secrets);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IMailHost, NoHost>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<MailSecrets>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<HmacRecipientHasher>(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var hasher = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<HmacRecipientHasher>(provider);
        var hash = hasher.Hash("mail-rate", "Ada@Example.com");
        Assert.NotEmpty(hash);
    }

    [Fact]
    public async Task AcsWithoutAConnectionStringDoesNotSend()
    {
        var sender = new AcsMailSender(
            new MailTemplateRenderer(),
            new MailModuleOptions { SenderAddress = "" },
            new MailSecrets(new Empty(), new NoHost()),
            new BrokenTransport(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AcsMailSender>.Instance);
        var result = await sender.SendAsync(new MailMessage(MailTemplateId.LoginCode, "a@b.co", "en", new Dictionary<string, string> { ["CODE"] = "1", ["ACTION_URL"] = "https://app.intochat.io" }), CancellationToken.None);
        Assert.Equal(MailSendOutcome.NotConfigured, result.Outcome);
    }

    [Fact]
    public async Task TheRateWrapperRefusesALimitedRecipient()
    {
        var inner = new RecordingSender();
        var hasher = new HmacRecipientHasher();
        hasher.UseKey("k");
        var proxies = new SingleProxy(new DeniedRate());
        var sender = new RateLimitedMailSender(inner, hasher, proxies);
        var result = await sender.SendAsync(new MailMessage(MailTemplateId.LoginCode, "a@b.co", "en", new Dictionary<string, string>()), CancellationToken.None);
        Assert.Equal(MailSendOutcome.RateLimited, result.Outcome);
        Assert.Equal(0, inner.Sent);
    }

    private sealed class Empty : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoHost : IMailHost
    {
        public IDisposable StampModule() => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class BrokenTransport : IAcsEmailTransport
    {
        public Task<string> SendAsync(string connectionString, Azure.Communication.Email.EmailMessage message, CancellationToken ct)
            => throw new InvalidOperationException("transport");
    }

    private sealed class RecordingSender : IMailSender
    {
        public int Sent { get; private set; }

        public Task<MailSendResult> SendAsync(MailMessage message, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(new MailSendResult(MailSendOutcome.Accepted, "ok", null));
        }
    }

    private sealed class DeniedRate : IMailRate
    {
        public Task<MailRateDecision> TryAcquire(MailTemplateId template) => Task.FromResult(new MailRateDecision(false, DateTimeOffset.UtcNow));
    }

    private sealed class SingleProxy(IMailRate rate) : INeuronProxies
    {
        public T Get<T>(NeuronId id) where T : class => (T)(object)rate;
    }

    private sealed class StubSecrets : DigitalBrain.Capabilities.Secrets.ISecrets
    {
        public Task<DigitalBrain.Capabilities.Secrets.SecretRef> Set(string name, string label, string value, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task Remove(string name, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<string> Resolve(DigitalBrain.Capabilities.Secrets.SecretRef secret, CancellationToken cancellationToken = default) => Task.FromResult("test-recipient-key");
        public Task<DigitalBrain.Capabilities.Secrets.SealedSecret> Seal(string context, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<byte[]> Open(string context, DigitalBrain.Capabilities.Secrets.SealedSecret sealedSecret, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DigitalBrain.Capabilities.Secrets.SecretRef> Named(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(new DigitalBrain.Capabilities.Secrets.SecretRef { Reference = "named:" + name, Label = name, Status = DigitalBrain.Capabilities.Secrets.SecretStatus.Set });
    }
}
