using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Identity.Directory;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalBrain.Platform.Tests.Unit.Identity;

public sealed class SeededAccountFacts
{
    private static readonly IOptions<AuthOptions> Open = Options.Create(new AuthOptions { Posture = IdentityPosture.Open });

    // A seed password is a development convenience; a secured host must never accept it, or the
    // first-party publisher would ship with a password everyone can read in the AppHost.
    [Fact]
    public async Task ASecuredHostNeverGivesASeededAccountItsConfiguredPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var options = new SeededAccountOptions();
        options.Accounts.Add(new() { Username = "intochat", Password = "intochat-development", DisplayName = "IntoChat" });
        var secured = Options.Create(new AuthOptions { Posture = IdentityPosture.Secured });

        await new SeededAccountsStartup(brain.Grains, Options.Create(options), secured, NullLogger<SeededAccountsStartup>.Instance).Execute(ct);

        Assert.NotNull(await brain.Grains.GetGrain<DigitalBrain.Platform.Identity.Authority.IPrincipal>("intochat").DefaultMembership());
        Assert.Null(await brain.Get<IIdentityDirectory>(IdentityGrains.Directory).AuthenticateAsync("intochat", "intochat-development", ct));
    }

    [Fact]
    public async Task SeededAccountsAreRegisteredOnceAndKeepTheirAccountAcrossRestarts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var options = new SeededAccountOptions();
        options.Accounts.Add(new() { Username = "intochat", Password = "intochat-development", DisplayName = "IntoChat" });
        var startup = new SeededAccountsStartup(brain.Grains, Options.Create(options), Open, NullLogger<SeededAccountsStartup>.Instance);

        await startup.Execute(ct);
        var directory = brain.Get<IIdentityDirectory>(IdentityGrains.Directory);
        var first = await directory.AuthenticateAsync("intochat", "intochat-development", ct);
        Assert.NotNull(first);
        Assert.Equal("IntoChat", first.DisplayName);

        await startup.Execute(ct);
        Assert.Equal(first, await directory.AuthenticateAsync("intochat", "intochat-development", ct));
    }

    [Fact]
    public async Task ASeededAccountWithoutAPasswordExistsButCannotSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var options = new SeededAccountOptions();
        options.Accounts.Add(new() { Username = "os", DisplayName = "DigitalBrain OS" });
        var startup = new SeededAccountsStartup(brain.Grains, Options.Create(options), Open, NullLogger<SeededAccountsStartup>.Instance);

        await startup.Execute(ct);
        await startup.Execute(ct);

        var membership = await brain.Grains.GetGrain<DigitalBrain.Platform.Identity.Authority.IPrincipal>("os").DefaultMembership();
        Assert.NotNull(membership);
        Assert.Null(await brain.Get<IIdentityDirectory>(IdentityGrains.Directory).AuthenticateAsync("os", "", ct));
    }

    [Fact]
    public async Task AChangedSeedPasswordLeavesTheExistingAccountUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().StartAsync(ct);
        var directory = brain.Get<IIdentityDirectory>(IdentityGrains.Directory);
        var existing = await directory.RegisterAsync("intochat", "original-password", "IntoChat", ct);
        var options = new SeededAccountOptions();
        options.Accounts.Add(new() { Username = "intochat", Password = "different-password", DisplayName = "IntoChat" });

        await new SeededAccountsStartup(brain.Grains, Options.Create(options), Open, NullLogger<SeededAccountsStartup>.Instance).Execute(ct);

        Assert.Equal(existing, await directory.AuthenticateAsync("intochat", "original-password", ct));
        Assert.Null(await directory.AuthenticateAsync("intochat", "different-password", ct));
    }
}
