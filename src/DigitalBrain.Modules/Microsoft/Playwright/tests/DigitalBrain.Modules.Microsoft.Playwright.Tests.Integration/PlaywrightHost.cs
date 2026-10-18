using DigitalBrain.Microsoft.Playwright;
using DigitalBrain.Testing.Fakes;
using DigitalBrain.Tools;
using Microsoft.Extensions.DependencyInjection;

[assembly: AssemblyFixture(typeof(DigitalBrain.Modules.Microsoft.Playwright.Tests.Integration.PlaywrightHost))]

namespace DigitalBrain.Modules.Microsoft.Playwright.Tests.Integration;

// The shared Playwright host over a fake browser session provider. Facts that script their own provider's timing
// and failures register it per brain and keep their own TestBrain ([Trait("host", "own")]).
public sealed class PlaywrightHost : BrainHostFixture
{
    public FakeBrowserSessionProvider Provider { get; } = new() { HangNavigation = true };

    protected override TestBrain Configure(TestBrain brain) => brain.WithModule<PlaywrightModule>().WithModule<ToolsModule>()
        .ConfigureServices(services => services.AddSingleton<IBrowserSessionProvider>(Provider));
}
