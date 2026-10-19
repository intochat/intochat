using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MODULE_NAMESPACE.Tests;

public sealed class MODULE_CLASSModuleFacts
{
    [Fact]
    public async Task TheModuleIsActiveAndItsSettingDefaultResolves()
    {
        await using var brain = await TestBrain.Create().WithModule<MODULE_CLASSModule>().StartAsync(TestContext.Current.CancellationToken);

        var options = brain.Services.GetRequiredService<MODULE_CLASSOptions>();

        Assert.Equal("eu", options.MODULE_SETTING);
    }
}
