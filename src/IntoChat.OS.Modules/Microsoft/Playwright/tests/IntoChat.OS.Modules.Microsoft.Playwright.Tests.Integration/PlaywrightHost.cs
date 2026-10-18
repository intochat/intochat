using IntoChat.OS.Microsoft.Playwright;
using IntoChat.OS.Testing.Fakes;
using IntoChat.OS.Tools;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(IntoChat.OS.Modules.Microsoft.Playwright.Tests.Integration.PlaywrightHost))]

namespace IntoChat.OS.Modules.Microsoft.Playwright.Tests.Integration;

// The shared Playwright host over a fake browser session provider. Facts that script their own provider's timing
// and failures register it per brain and keep their own TestBrain ([Trait("host", "own")]).
public sealed class PlaywrightHost : BrainHostFixture
{
    public FakeBrowserSessionProvider Provider { get; } = new() { HangNavigation = true };

    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<PlaywrightModule>().WithModule<ToolsModule>()
        .ConfigureServices(services => services.AddSingleton<IBrowserSessionProvider>(Provider));
}
