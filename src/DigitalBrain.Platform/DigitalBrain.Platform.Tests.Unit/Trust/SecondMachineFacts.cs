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
    [Fact(Timeout = 180_000)]
    public async Task ASecondOwnMachineStartsHealthyWhileTheFirstHoldsTheCore()
    {
        var ct = TestContext.Current.CancellationToken;
        var errors = new ErrorLog();
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await a.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        var scope = BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;
        Assert.Contains(PartId.Core(scope), a.SiloServices.GetRequiredService<PartLogs>().Opened);

        await using var b = await ModuleTest.Create().WithTrust(trust).WithPartLog()
            .ConfigureSilo(silo => silo.Services.AddSingleton<ILoggerProvider>(errors))
            .StartAsync(ct);
        var birth = b.SiloServices.GetRequiredService<OwnerBirth>();
        await birth.Born.WaitAsync(ct);
        // The live race: the trust side named B the new brain's machine while A already held the core.
        var owner = ModuleTest.DefaultAccount;
        birth.Await(new BrainProvision(new Member { PrincipalId = owner, AccountId = owner, BrainId = owner, Role = MemberRole.Owner, DisplayName = owner }, NewBrain: true));
        await birth.Execute(ct);
        await birth.Born.WaitAsync(ct);

        Assert.DoesNotContain(PartId.Core(scope), b.SiloServices.GetRequiredService<PartLogs>().Opened);
        Assert.Empty(errors.Lines);
    }

    [Fact(Timeout = 180_000)]
    public async Task TwoMachinesRacingForAPartKeyEndWithOneKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await using var b = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        var aKeys = a.SiloServices.GetRequiredService<IPartKeys>();
        var bKeys = b.SiloServices.GetRequiredService<IPartKeys>();
        var part = PartId.ForApp(BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id, "race");

        var keys = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => (i % 2 == 0 ? aKeys : bKeys).Key(part, ModuleTest.DefaultAccount, ct)));

        Assert.Single(keys.Select(Convert.ToBase64String).Distinct());
    }

    [Fact(Timeout = 180_000)]
    public async Task AMachineWhoseCustodyCannotUnwrapAPartKeyIsRefusedClearly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var trust = await TrustHost.StartAsync(ct);
        await using var a = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await using var b = await ModuleTest.Create().WithTrust(trust).WithPartLog()
            .WithConfiguration(DigitalBrainNames.MasterKeyConfigurationKey, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)))
            .StartAsync(ct);
        var part = PartId.ForApp(BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id, "custody");
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
