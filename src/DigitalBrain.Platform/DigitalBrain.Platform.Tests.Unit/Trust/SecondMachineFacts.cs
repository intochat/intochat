using DigitalBrain.Framework;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Platform.Tests.Unit.Trust;

// R73/R74: a second own machine of the same owner joins while the first holds the core. It registers, never opens
// the core, and opens a part only under the one key the keyring holds for it.
public sealed class SecondMachineFacts
{
    internal static readonly string CoreHolder = $"{PartsOptions.Key}:{nameof(PartsOptions.CoreHolder)}";

    [Fact(Timeout = 180_000)]
    public async Task ASecondOwnMachineStartsHealthyWhileTheFirstHoldsTheCore()
    {
        var ct = TestContext.Current.CancellationToken;
        var errors = new ErrorLog();
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await TestBrain.Create().WithTrust(trust).WithPartLog().WithConfiguration(CoreHolder, "true").StartAsync(ct);
        await a.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;
        Assert.Contains(PartId.Core(scope), a.SiloServices.GetRequiredService<PartLogs>().Opened);

        await using var b = await TestBrain.Create().WithTrust(trust).WithPartLog()
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(errors))
            .StartAsync(ct);
        var birth = b.SiloServices.GetRequiredService<OwnerBirth>();
        await birth.Born.WaitAsync(ct);
        // The live race: the trust side named B the new brain's machine while A already held the core.
        var owner = AccountSession.DefaultLogin;
        birth.Await(new BrainProvision(new Member { PrincipalId = owner, AccountId = owner, BrainId = owner, Role = MemberRole.Owner, DisplayName = owner }, NewBrain: true));
        await birth.Execute(ct);
        await birth.Born.WaitAsync(ct);

        Assert.DoesNotContain(PartId.Core(scope), b.SiloServices.GetRequiredService<PartLogs>().Opened);
        Assert.Empty(errors.Lines);
    }

    // R76: the core has a designated holder; a machine that is not it never takes the core at boot, even first.
    [Fact(Timeout = 180_000)]
    public async Task OnlyTheCoreHolderAcquiresTheCoreAtBoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var core = PartId.Core(BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id);
        await using var trust = await TrustHost.StartAsync(ct);
        await using var b = await TestBrain.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await b.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        Assert.DoesNotContain(core, b.SiloServices.GetRequiredService<PartLogs>().Opened);

        await using var a = await TestBrain.Create().WithTrust(trust).WithPartLog().WithConfiguration(CoreHolder, "true").StartAsync(ct);
        await a.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);

        Assert.Contains(core, a.SiloServices.GetRequiredService<PartLogs>().Opened);
        Assert.DoesNotContain(core, b.SiloServices.GetRequiredService<PartLogs>().Opened);
    }

    // R77: the holder's boot birth and every later read of its core go through one PartLogs under one identity.
    [Fact(Timeout = 180_000)]
    public async Task TheCoreHolderReopensItsOwnCoreAfterBirth()
    {
        var ct = TestContext.Current.CancellationToken;
        var errors = new ErrorLog();
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await TestBrain.Create().WithTrust(trust).WithPartLog().WithConfiguration(CoreHolder, "true")
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(errors))
            .StartAsync(ct);
        await a.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

        await a.Grains.GetGrain<IBrain>(scope).Read();
        var core = await a.SiloServices.GetRequiredService<PartLogs>().For(PartId.Core(scope));

        Assert.False(core.Closed);
        Assert.DoesNotContain(errors.Lines, line => line.Contains("held by machine", StringComparison.Ordinal));
    }

    [Fact(Timeout = 180_000)]
    public async Task AMachineHasOneIdentityAcrossItsDeploymentFolders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await TestBrain.Create().WithTrust(trust).WithPartLog().WithConfiguration(CoreHolder, "true").StartAsync(ct);
        await a.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        var core = PartId.Core(BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id);
        var machine = a.SiloServices.GetRequiredService<DigitalBrain.Platform.Sequencer.MachineIdentity>();
        var directory = a.SiloServices.GetRequiredService<global::Microsoft.Extensions.Options.IOptions<PartsOptions>>().Value.MachineDirectory;
        var deployment = await a.SiloServices.GetRequiredService<TrustDeployment>().Resolve();

        using var root = DigitalBrain.Platform.Sequencer.MachineIdentity.LoadOrCreate(Path.Combine(directory, "identity.key"));
        var head = await a.SiloServices.GetRequiredService<ISequencerClient>().Get(DigitalBrain.Platform.Sequencer.HeadRecord.HashOf(core.Value));

        Assert.Equal(root.MachineId, machine.MachineId);
        Assert.Equal(machine.MachineId, head!.Holder);
        Assert.False(File.Exists(Path.Combine(deployment, "identity.key")));
    }

    [Fact(Timeout = 180_000)]
    public async Task TwoMachinesRacingForAPartKeyEndWithOneKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await TestBrain.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await using var b = await TestBrain.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        var aKeys = a.SiloServices.GetRequiredService<IPartKeys>();
        var bKeys = b.SiloServices.GetRequiredService<IPartKeys>();
        var part = PartId.ForApp(BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id, "race");

        var keys = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => (i % 2 == 0 ? aKeys : bKeys).Key(part, AccountSession.DefaultLogin, ct)));

        Assert.Single(keys.Select(Convert.ToBase64String).Distinct());
    }

    [Fact(Timeout = 180_000)]
    public async Task AMachineWhoseCustodyCannotUnwrapAPartKeyIsRefusedClearly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await TestBrain.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await using var b = await TestBrain.Create().WithTrust(trust).WithPartLog()
            .WithConfiguration(DigitalBrainNames.MasterKeyConfigurationKey, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))
            .StartAsync(ct);
        var part = PartId.ForApp(BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id, "custody");
        var aLogs = a.SiloServices.GetRequiredService<PartLogs>();
        await (await aLogs.For(part)).Append($"counter/{part.Value}/c1", "counter", RecordChange.Write, [1], []);
        await aLogs.Release(part);

        var refused = await Assert.ThrowsAsync<ServiceUnavailableException>(() => b.SiloServices.GetRequiredService<PartLogs>().For(part));
        Assert.Contains("not unwrappable on this machine", refused.Message, StringComparison.Ordinal);
    }

    private sealed class ErrorLog : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger(ErrorLog log, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel)) { return; }
                lock (log.Lines) { log.Lines.Add($"{category}: {formatter(state, exception)} {exception?.Message}"); }
            }
        }
    }
}
